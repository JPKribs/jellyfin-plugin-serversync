using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
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
        PeopleSyncTableManager manager)
        : base(logger, manager, clientFactory, configManager)
    {
        _libraryManager = libraryManager;
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

    // <c>BuildRecordAsync</c> is HTTP-free now (source data comes from the
    // bulk <c>/Persons</c> fetch, image data from <c>BaseItemDto.ImageTags</c>);
    // the only blocking work per item is the local file-size stat for each
    // matched person's images. A modest parallelism here lets that stat I/O
    // overlap without contending on Jellyfin's library SQLite.
    /// <inheritdoc />
    protected override int BuildRecordParallelism => 4;

    /// <inheritdoc />
    protected override bool IsEnabled()
    {
        var config = ConfigManager.Configuration;
        return config.EnablePeopleSync
            && !string.IsNullOrWhiteSpace(config.SourceServerUrl)
            && !string.IsNullOrWhiteSpace(config.SourceServerApiKey);
    }

    // Bulk fetch + in-memory join: enumerate local Person items, then pull
    // the full source Person catalog via paginated <c>/Persons?fields=…</c>
    // calls (no <c>searchTerm</c>) and intersect by name in memory. The
    // previous per-name fan-out cost one HTTP round-trip per local person
    // (130k+ requests on a real library, 2.5 hours wall-clock); this
    // version trades that for ~N/1000 paginated requests and a hashtable
    // lookup per local name.
    /// <inheritdoc />
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
        IReadOnlyList<SdkBaseItemDto> sourcePersons;
        try
        {
            sourcePersons = await Client.GetAllPersonsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Source returned an error (or no real answer). Skip pruning this run
            // so persons that still exist aren't deleted, and enumerate nothing.
            MarkSourceUnavailable("source server unavailable bulk-fetching persons");
            Logger.LogWarning(ex, "Failed to bulk-fetch persons from source; skipping prune this run");
            return Array.Empty<SdkBaseItemDto>();
        }

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
    protected override async Task<PeopleSyncItem?> BuildRecordAsync(
        BaseItemDto source,
        IReadOnlyDictionary<string, PeopleSyncItem> existing,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(source.Name))
        {
            return null;
        }

        // Look up local person from the cache built in GetListAsync. Going
        // back to ILibraryManager here under parallel load was producing
        // SQLite "database is locked" errors against Jellyfin's main DB.
        if (_localPersonsByName == null
            || !_localPersonsByName.TryGetValue(source.Name, out var localPerson))
        {
            return null;
        }

        // Reuse existing record (preserves Status if Ignored) or create new.
        var record = existing.TryGetValue(source.Name, out var prev)
            ? prev
            : new PeopleSyncItem { PersonName = source.Name };

        record.SourcePersonId = source.Id?.ToString("N", CultureInfo.InvariantCulture);
        record.LocalPersonId = localPerson.Id.ToString("N", CultureInfo.InvariantCulture);

        // Build metadata blobs for both sides; SyncableValue.RecomputeSourceHash
        // ensures the SourceHash field is populated for the Compare fast-path.
        record.Metadata.Source = PeopleSyncMergeService.BuildSourceMetadata(source);
        record.Metadata.Local = PeopleSyncMergeService.BuildLocalMetadata(localPerson);
        record.Metadata.RecomputeSourceHash();

        var config = ConfigManager.Configuration;
        if (config.PeopleSyncImages)
        {
            var (sourceImg, localImg) = PeopleSyncMergeService.PopulateImageData(source, localPerson);

            // Enrich source-side image manifest with real Size/Width/Height
            // via /Items/{id}/Images. PopulateImageData builds the source side
            // tag-only (Size=0) from BaseItemDto.ImageTags — that path is
            // cheap but leaves the comparator with no honest signal to
            // compare against the sized local manifest. Without enrichment
            // here, the comparator's tag-only-vs-sized fallback fires on
            // every row, every refresh — every row queues, every row
            // re-applies, every refresh wastes the bandwidth of redownloading
            // images that already match. One per-person HTTP at refresh time
            // gives the comparator real numbers, so rows that genuinely
            // match Source==Local stay Synced and only true divergences
            // queue.
            if (Client != null
                && !string.IsNullOrEmpty(record.SourcePersonId)
                && Guid.TryParse(record.SourcePersonId, out var sourcePersonGuid))
            {
                try
                {
                    sourceImg = await ImageManifestEnricher.EnrichAsync(
                        sourceImg,
                        sourcePersonGuid,
                        Client,
                        Logger,
                        source.Name,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Source image enrichment failed for {PersonName}; comparator will fall back to tag-only", source.Name);
                }
            }

            // Carry sizes enrichment couldn't measure this run forward from the
            // prior manifest for unchanged images, so a transient enrichment
            // failure doesn't drop the row back to tag-only and read as a
            // phantom change against the sized local side.
            record.Images.UpdateSource(
                ImageManifestEnricher.CarryForwardSizes(sourceImg, record.Images.Source));
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

        return record;
    }

    /// <inheritdoc />
    protected override string ExtractKey(PeopleSyncItem record) => record.PersonName;

    /// <inheritdoc />
    protected override void RecordRunCompleted(Jellyfin.Plugin.ServerSync.Configuration.PluginConfiguration config, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.LastPeopleSyncTime = utcNow;
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
