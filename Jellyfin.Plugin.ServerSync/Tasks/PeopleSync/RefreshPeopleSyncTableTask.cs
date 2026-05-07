using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    /// People's <c>BuildRecordAsync</c> issues a per-person
    /// <c>GetItemImageInfoAsync</c> HTTP call for every locally-matched
    /// person when image sync is enabled. Most persons get filtered earlier
    /// (no local match), but on a library with many matched persons this
    /// adds up; parallelism keeps the build phase from being HTTP-bound.
    /// </remarks>
    protected override int BuildRecordParallelism => 8;

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
    /// Local-first fetch: enumerate the local Person items, then for each
    /// distinct name make a per-name <c>/Persons/{name}</c> call against the
    /// source server (parallel, bounded). The previous "fetch all source
    /// persons then filter by local match" approach pulled tens of thousands
    /// of person records over the wire just to find a small subset that has
    /// a local correlate; this is dramatically cheaper when the local
    /// library has fewer persons than the source.
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

        var localNames = _localPersonsByName.Keys.ToList();

        Logger.LogInformation(
            "{Task}: enumerated {Count} local persons; fetching matching source persons",
            Name,
            localNames.Count);

        progress.Report(10);

        if (localNames.Count == 0)
        {
            return Array.Empty<SdkBaseItemDto>();
        }

        // Step 2: per-name fetch against source. /Persons/{name} returns the
        // full person entity; combined with the follow-up /Items?Ids= that
        // GetPersonByNameAsync issues, every match yields a fully-populated
        // BaseItemDto. Bounded parallelism so we don't melt the source.
        var matched = new ConcurrentBag<SdkBaseItemDto>();
        var processed = 0;
        var total = Math.Max(localNames.Count, 1);

        await Parallel.ForEachAsync(
            localNames,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (name, ct) =>
            {
                try
                {
                    var person = await Client.GetPersonByNameAsync(name, ct).ConfigureAwait(false);
                    if (person != null)
                    {
                        matched.Add(person);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Failed to fetch source person {Name}", name);
                }
                finally
                {
                    var done = Interlocked.Increment(ref processed);
                    progress.Report(Math.Min(100, 10 + (90.0 * done / total)));
                }
            }).ConfigureAwait(false);

        var result = matched.ToList();
        Logger.LogInformation(
            "{Task}: matched {Matched}/{Local} local persons against source",
            Name,
            result.Count,
            localNames.Count);

        progress.Report(100);
        return result;
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
