using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using SdkBaseItemDto = Jellyfin.Sdk.Generated.Models.BaseItemDto;
using TaskTriggerInfo = MediaBrowser.Model.Tasks.TaskTriggerInfo;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Refresh phase for People sync. Fetches all source persons, looks up each
/// by name on the local server, and writes a snapshot row for the matched
/// ones. Persons without a local match are skipped (no row written).
/// </summary>
public class RefreshPeopleSyncTableTask : RefreshSyncTaskBase<PeopleSyncItem, BaseItemDto, string>
{
    private readonly ILibraryManager _libraryManager;
    private readonly PeopleSyncTableService _peopleService;

    /// <summary>
    /// Per-run cache of local Person items keyed by Name. Built by
    /// <see cref="GetListAsync"/>, consumed by <see cref="BuildRecordAsync"/>
    /// so the build phase doesn't re-hit Jellyfin's library SQLite for every
    /// item — that contention was producing
    /// <c>SQLite Error 5: 'database is locked'</c> under parallelism.
    /// </summary>
    private Dictionary<string, BaseItem>? _localPersonsByName;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public RefreshPeopleSyncTableTask(
        ILogger<RefreshPeopleSyncTableTask> logger,
        ILibraryManager libraryManager,
        ISourceServerClientFactory clientFactory,
        IPluginConfigurationManager configManager,
        PeopleSyncTableService peopleService,
        PeopleSyncTableManager manager)
        : base(logger, manager, clientFactory, configManager)
    {
        _libraryManager = libraryManager;
        _peopleService = peopleService;
    }

    /// <inheritdoc />
    public override string Name => "Refresh People Sync Table";

    /// <inheritdoc />
    public override string Key => "ServerSyncRefreshPeopleTable";

    /// <inheritdoc />
    public override string Description => "Scans source persons, matches against local persons, and stores snapshots in the people sync table.";

    /// <inheritdoc />
    public override string Category => "People Sync";

    /// <inheritdoc />
    protected override string ModuleMutexKey => "People";

    /// <inheritdoc />
    /// <remarks>
    /// <c>BuildRecordAsync</c> is HTTP-free now (source data comes from the
    /// bulk <c>/Persons</c> fetch, image data from <c>BaseItemDto.ImageTags</c>);
    /// the only blocking work per item is the local file-size stat for each
    /// matched person's images. A modest parallelism here lets that stat I/O
    /// overlap without contending on Jellyfin's library SQLite.
    /// </remarks>
    protected override int BuildRecordParallelism => 4;

    /// <inheritdoc />
    protected override bool IsEnabled()
    {
        var config = ConfigManager.Configuration;
        return config.EnablePeopleSync
            && !string.IsNullOrWhiteSpace(config.SourceServerUrl)
            && !string.IsNullOrWhiteSpace(config.SourceServerApiKey);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Bulk fetch + in-memory join: enumerate local Person items, then pull
    /// the full source Person catalog via paginated <c>/Persons?fields=…</c>
    /// calls (no <c>searchTerm</c>) and intersect by name in memory. The
    /// previous per-name fan-out cost one HTTP round-trip per local person
    /// (130k+ requests on a real library, 2.5 hours wall-clock); this
    /// version trades that for ~N/1000 paginated requests and a hashtable
    /// lookup per local name.
    /// </remarks>
    protected override async Task<IList<SdkBaseItemDto>> GetListAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (Client == null)
        {
            return Array.Empty<SdkBaseItemDto>();
        }

        progress.Report(2);

        // Step 1: enumerate local Person items once and cache them for
        // BuildRecordAsync. Doing this once up front (rather than per-item)
        // avoids hammering Jellyfin's library SQLite during the parallel
        // build phase — that was producing "database is locked" errors.
        var localPersons = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Person }
        }) ?? new List<BaseItem>();

        _localPersonsByName = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in localPersons)
        {
            if (!string.IsNullOrEmpty(p.Name) && !_localPersonsByName.ContainsKey(p.Name))
            {
                _localPersonsByName[p.Name] = p;
            }
        }

        Logger.LogInformation(
            "{Task}: enumerated {Count} local persons; bulk-fetching source persons",
            Name,
            _localPersonsByName.Count);

        progress.Report(10);

        if (_localPersonsByName.Count == 0)
        {
            return Array.Empty<SdkBaseItemDto>();
        }

        // Step 2: bulk-fetch the entire source Person catalog in pages.
        // Returns BaseItemDto with the same field set GetPersonByNameAsync
        // used, so downstream metadata blobs are byte-identical to the
        // previous per-name path.
        var sourcePersons = await Client.GetAllPersonsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        progress.Report(80);

        // Step 3: intersect by name in memory. Source duplicates (same name
        // appearing twice on source) keep the first occurrence — the local
        // dictionary uses the same first-wins rule above.
        var matched = new List<SdkBaseItemDto>(_localPersonsByName.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var person in sourcePersons)
        {
            if (string.IsNullOrEmpty(person.Name)) continue;
            if (!_localPersonsByName.ContainsKey(person.Name)) continue;
            if (!seen.Add(person.Name)) continue;
            matched.Add(person);
        }

        Logger.LogInformation(
            "{Task}: matched {Matched}/{Local} local persons against {SourceTotal} source persons",
            Name,
            matched.Count,
            _localPersonsByName.Count,
            sourcePersons.Count);

        progress.Report(100);
        return matched;
    }

    /// <inheritdoc />
    protected override Task<PeopleSyncItem?> BuildRecordAsync(
        BaseItemDto source,
        IReadOnlyDictionary<string, PeopleSyncItem> existing,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (string.IsNullOrEmpty(source.Name))
        {
            return Task.FromResult<PeopleSyncItem?>(null);
        }

        // Look up local person from the cache built in GetListAsync. Going
        // back to ILibraryManager here under parallel load was producing
        // SQLite "database is locked" errors against Jellyfin's main DB.
        if (_localPersonsByName == null
            || !_localPersonsByName.TryGetValue(source.Name, out var localPerson))
        {
            return Task.FromResult<PeopleSyncItem?>(null);
        }

        // Reuse existing record (preserves Status if Ignored) or create new.
        var record = existing.TryGetValue(source.Name, out var prev)
            ? prev
            : new PeopleSyncItem { PersonName = source.Name };

        record.SourcePersonId = source.Id?.ToString("N", CultureInfo.InvariantCulture);
        record.LocalPersonId = localPerson.Id.ToString("N", CultureInfo.InvariantCulture);

        // Build metadata blobs for both sides; SyncableValue.RecomputeSourceHash
        // ensures the SourceHash field is populated for the Compare fast-path.
        record.Metadata.Source = PeopleSyncTableService.BuildSourceMetadata(source);
        record.Metadata.Local = PeopleSyncTableService.BuildLocalMetadata(localPerson);
        record.Metadata.RecomputeSourceHash();

        // Image manifests — image data comes from the bulk /Persons response
        // (no per-person HTTP call). UpdateSource produces a comparator-built
        // hash so the SourceHash short-circuit works correctly across
        // refreshes.
        var config = ConfigManager.Configuration;
        if (config.PeopleSyncImages)
        {
            var (sourceImg, localImg) = _peopleService.PopulateImageData(source, localPerson);
            record.Images.UpdateSource(sourceImg);
            record.Images.Local = localImg;
        }
        else
        {
            // Clear image fields if image sync is disabled — the Compare phase
            // then sees no Image-side changes regardless of what was stored.
            record.Images.Source = null;
            record.Images.Local = null;
            record.Images.SourceHash = null;
        }

        return Task.FromResult<PeopleSyncItem?>(record);
    }

    /// <inheritdoc />
    protected override string ExtractKey(PeopleSyncItem record) => record.PersonName;

    /// <inheritdoc />
    protected override Task FinalizeAsync(CancellationToken cancellationToken)
    {
        ConfigManager.Configuration.LastPeopleSyncTime = DateTime.UtcNow;
        ConfigManager.SaveConfiguration();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = MediaBrowser.Model.Tasks.TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(12).Ticks
        }
    };
}
