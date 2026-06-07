using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using TaskTriggerInfo = MediaBrowser.Model.Tasks.TaskTriggerInfo;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Source-side work item for the Metadata refresh task: a (library mapping,
/// source item, isFolder) triple turned into a
/// <see cref="MetadataSyncItem"/> by
/// <see cref="MetadataSyncTableService.BuildRecordAsync"/>.
/// </summary>
public sealed record MetadataWork(LibraryMapping LibraryMapping, BaseItemDto SourceItem, bool IsFolder);

/// <summary>
/// Refresh phase for Metadata sync. Walks every enabled library, fetches
/// items (with optional folder-items pass) from each, and builds a
/// <see cref="MetadataSyncItem"/> per (library, item) pair.
/// </summary>
public class RefreshMetadataSyncTableTask
    : RefreshSyncTaskBase<MetadataSyncItem, MetadataWork, (string SourceLibraryId, string SourceItemId)>
{
    private readonly MetadataSyncTableService _metadataService;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public RefreshMetadataSyncTableTask(
        ILogger<RefreshMetadataSyncTableTask> logger,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        MetadataSyncTableService metadataService,
        MetadataSyncTableManager manager)
        : base(logger, manager, clientFactory, configManager)
    {
        _metadataService = metadataService;
    }

    /// <inheritdoc />
    public override string Name => "Refresh Metadata Sync Table";

    /// <inheritdoc />
    public override string Key => "ServerSyncRefreshMetadataTable";

    /// <inheritdoc />
    public override string Description => "Scans source and local servers for metadata differences and updates the metadata sync table.";

    /// <inheritdoc />
    public override string Category => "Metadata Sync";

    /// <inheritdoc />
    protected override string ModuleMutexKey => "Metadata";

    // Metadata's <c>BuildRecordAsync</c> issues a per-item
    // <c>GetItemImageInfoAsync</c> HTTP call when image sync is enabled. With
    // libraries in the tens of thousands of items, serializing these round-
    // trips makes a refresh take hours. 8 parallel builds turn that into
    // minutes without overwhelming a typical home Jellyfin source.
    /// <inheritdoc />
    protected override int BuildRecordParallelism => 8;

    /// <inheritdoc />
    protected override bool IsEnabled()
    {
        var config = ConfigManager.Configuration;
        if (!config.EnableMetadataSync) return false;
        if (string.IsNullOrWhiteSpace(config.SourceServerUrl) || string.IsNullOrWhiteSpace(config.SourceServerApiKey)) return false;
        if (config.GetEnabledLibraryMappings().Count == 0) return false;
        // At least one category enabled.
        return config.MetadataSyncMetadata || config.MetadataSyncImages
            || config.MetadataSyncPeople || config.MetadataSyncStudios;
    }

    // Two-phase fetch instead of "bulk-fetch everything heavy":
    //   <list type="number">
    //   <item>Enumerate local items per library and build a path lookup.</item>
    //   <item>Light source discovery — paginate every library asking for
    //   only <c>Path</c> + <c>Id</c>. Per-page payload is tiny.</item>
    //   <item>Filter source items to those whose translated path exists
    //   locally — that's the actual sync set.</item>
    //   <item>Heavy fetch by IDs — only request full metadata fields for
    //   matched items, batched.</item>
    //   </list>
    // On a typical install where local is a subset of source, this
    // dramatically reduces both bytes-over-wire and the build phase's
    // work — we no longer fetch full metadata for tens of thousands of
    // source items the user doesn't have.
    /// <inheritdoc />
    protected override async Task<IList<MetadataWork>> GetListAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (Client == null)
        {
            return Array.Empty<MetadataWork>();
        }

        var config = ConfigManager.Configuration;
        var enabledMappings = config.GetEnabledLibraryMappings();
        var includeFolderItems = config.MetadataSyncFolderItems;
        var fields = BuildRequestedFields(config);

        var leafTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video };
        var folderTypes = new[]
        {
            BaseItemKind.Series,
            BaseItemKind.Season,
            BaseItemKind.MusicAlbum,
            BaseItemKind.MusicArtist,
            BaseItemKind.BoxSet
        };

        progress.Report(2);

        // Phase 1: enumerate local items per library. This is in-process and
        // fast — Jellyfin's library DB is already loaded.
        var localByLibrary = new Dictionary<string, (Dictionary<string, BaseItem> Leaves, Dictionary<string, BaseItem> Folders)>();
        foreach (var mapping in enabledMappings)
        {
            if (string.IsNullOrEmpty(mapping.LocalLibraryId)) continue;
            if (!Guid.TryParse(mapping.LocalLibraryId, out var localLibraryGuid)) continue;
            localByLibrary[mapping.SourceLibraryId] = _metadataService.GetLocalItemsByPath(localLibraryGuid);
        }

        progress.Report(8);

        // Phase 2: lightweight discovery — find the source IDs of items whose
        // translated paths exist locally. We collect (mapping, sourceId,
        // isFolder) triples; the source-side metadata isn't materialized yet.
        var matchedLeavesByMapping = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<Guid>>();
        var matchedFoldersByMapping = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<Guid>>();

        await Parallel.ForEachAsync(
            enabledMappings,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (mapping, ct) =>
            {
                if (!Guid.TryParse(mapping.SourceLibraryId, out var sourceLibraryId)) return;
                if (!localByLibrary.TryGetValue(mapping.SourceLibraryId, out var localPaths)) return;

                var leafBag = matchedLeavesByMapping.GetOrAdd(mapping.SourceLibraryId, _ => new System.Collections.Concurrent.ConcurrentBag<Guid>());
                var folderBag = matchedFoldersByMapping.GetOrAdd(mapping.SourceLibraryId, _ => new System.Collections.Concurrent.ConcurrentBag<Guid>());

                await DiscoverMatchingIdsAsync(mapping, sourceLibraryId, leafTypes, localPaths.Leaves, leafBag, ct).ConfigureAwait(false);
                if (includeFolderItems)
                {
                    await DiscoverMatchingIdsAsync(mapping, sourceLibraryId, folderTypes, localPaths.Folders, folderBag, ct).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

        var totalMatched = matchedLeavesByMapping.Values.Sum(b => b.Count) + matchedFoldersByMapping.Values.Sum(b => b.Count);
        Logger.LogInformation(
            "{Task}: discovery matched {Total} items across {Libraries} libraries",
            Name,
            totalMatched,
            enabledMappings.Count);

        progress.Report(40);

        if (totalMatched == 0)
        {
            return Array.Empty<MetadataWork>();
        }

        // Phase 3: batch-fetch full metadata only for matched IDs. Each
        // library's matched IDs are batched independently so we keep track
        // of which mapping a batch belongs to.
        var work = new System.Collections.Concurrent.ConcurrentBag<MetadataWork>();
        var fetched = 0;
        var heavyDenominator = Math.Max(totalMatched, 1);

        // Drive batching from here (rather than inside GetItemsByIdsAsync) so
        // progress ticks after every page lands instead of only after the
        // whole mapping finishes — the original implementation appeared
        // frozen at ~24% on libraries with tens of thousands of matched
        // items because no callback fired during the multi-minute serial
        // chunk loop.
        //
        // Batch size capped at 50 (matches the SDK default). I tried bumping
        // it to 200 to cut round-trip count, but that produced HTTP 414
        // (URI Too Long) on real-world setups — many reverse proxies (nginx
        // default large_client_header_buffers, IIS request filtering, etc.)
        // cap header size at 4–8KB, and 200 IDs × 32 hex chars + Fields=
        // and other query overhead pushes past that. 50 IDs ≈ 1.6KB of
        // ?Ids= content plus ~500 bytes of other query string, comfortably
        // under any reasonable proxy limit.
        //
        // Within a single mapping, run the chunk fetches in parallel
        // (bounded). Combined with the outer 4-way mapping parallelism this
        // gives up to 16 concurrent requests against the source. Most
        // self-hosted Jellyfin servers handle that fine; if it ever turns
        // out to overwhelm a weak source, the per-mapping cap is the easy
        // knob to lower.
        const int heavyBatchSize = 50;
        const int chunksPerMappingParallelism = 4;
        async Task FetchHeavyAsync(LibraryMapping mapping, IReadOnlyList<Guid> ids, bool isFolder, CancellationToken ct)
        {
            if (ids.Count == 0) return;

            // Pre-slice into chunks so Parallel.ForEachAsync can drive them.
            var chunks = new List<IReadOnlyList<Guid>>((ids.Count + heavyBatchSize - 1) / heavyBatchSize);
            for (var i = 0; i < ids.Count; i += heavyBatchSize)
            {
                var size = Math.Min(heavyBatchSize, ids.Count - i);
                var chunk = new List<Guid>(size);
                for (var j = i; j < i + size; j++)
                {
                    chunk.Add(ids[j]);
                }

                chunks.Add(chunk);
            }

            await Parallel.ForEachAsync(
                chunks,
                new ParallelOptions { MaxDegreeOfParallelism = chunksPerMappingParallelism, CancellationToken = ct },
                async (chunk, innerCt) =>
                {
                    var items = await Client.GetItemsByIdsAsync(chunk, fields, batchSize: heavyBatchSize, cancellationToken: innerCt).ConfigureAwait(false);
                    foreach (var item in items)
                    {
                        work.Add(new MetadataWork(mapping, item, isFolder));
                        var done = Interlocked.Increment(ref fetched);
                        progress.Report(40 + (60.0 * done / heavyDenominator));
                    }
                }).ConfigureAwait(false);
        }

        await Parallel.ForEachAsync(
            enabledMappings,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (mapping, ct) =>
            {
                if (matchedLeavesByMapping.TryGetValue(mapping.SourceLibraryId, out var leafBag) && !leafBag.IsEmpty)
                {
                    await FetchHeavyAsync(mapping, leafBag.ToArray(), false, ct).ConfigureAwait(false);
                }

                if (includeFolderItems
                    && matchedFoldersByMapping.TryGetValue(mapping.SourceLibraryId, out var folderBag)
                    && !folderBag.IsEmpty)
                {
                    await FetchHeavyAsync(mapping, folderBag.ToArray(), true, ct).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

        progress.Report(100);
        return work.ToList();
    }

    /// <summary>
    /// Phase-2 discovery helper: paginate the source library asking only for
    /// item paths, translate each to a local path, and add the source IDs
    /// whose translated path exists in <paramref name="localPaths"/> to the
    /// shared bag.
    /// </summary>
    private async Task DiscoverMatchingIdsAsync(
        LibraryMapping mapping,
        Guid sourceLibraryId,
        BaseItemKind[] includeTypes,
        Dictionary<string, BaseItem> localPaths,
        System.Collections.Concurrent.ConcurrentBag<Guid> matchedIds,
        CancellationToken cancellationToken)
    {
        if (Client == null || localPaths.Count == 0) return;

        const int pageSize = 1000;
        var startIndex = 0;
        var consecutiveErrors = 0;
        const int maxConsecutiveErrors = 3;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            BaseItemDtoQueryResult? page;
            try
            {
                page = await Client.GetLibraryItemPathsAsync(sourceLibraryId, includeTypes, startIndex, pageSize, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Discovery page failed for library {Library} at index {Index}", mapping.SourceLibraryName, startIndex);

                // Any source error means this run can't be sure it enumerated
                // every item, so the whole run skips pruning. Keep trying a few
                // more pages before giving up the library so reachable items
                // still upsert.
                MarkSourceUnavailable($"source server unavailable during '{mapping.SourceLibraryName}' discovery");

                if (++consecutiveErrors >= maxConsecutiveErrors)
                {
                    return;
                }

                continue;
            }

            consecutiveErrors = 0;

            if (page?.Items == null || page.Items.Count == 0)
            {
                return;
            }

            foreach (var item in page.Items)
            {
                if (!item.Id.HasValue || string.IsNullOrEmpty(item.Path))
                {
                    continue;
                }

                if (mapping.FilterMode != Models.Configuration.LibraryFilterMode.AllowAll
                    && mapping.FilteredItems?.Count > 0
                    && PathUtilities.IsItemFiltered(item.Path, mapping.SourceRootPath, mapping.FilterMode, mapping.FilteredItems))
                {
                    continue;
                }

                var localPath = PathUtilities.TranslatePath(item.Path, mapping.SourceRootPath, mapping.LocalRootPath);
                if (localPaths.ContainsKey(localPath))
                {
                    matchedIds.Add(item.Id.Value);
                }
            }

            if (page.TotalRecordCount.HasValue && startIndex + page.Items.Count >= page.TotalRecordCount.Value)
            {
                return;
            }

            if (page.Items.Count < pageSize)
            {
                return;
            }

            startIndex += page.Items.Count;
        }
    }

    /// <summary>
    /// Builds the minimum <see cref="ItemFields"/> set that satisfies the
    /// user's enabled metadata categories. Skipping unused fields (notably
    /// <see cref="ItemFields.People"/>, which can dominate the response on
    /// movie/episode libraries) drops the per-page payload substantially.
    /// </summary>
    private static ItemFields[] BuildRequestedFields(PluginConfiguration config)
    {
        var fields = new List<ItemFields>
        {
            // Always required for path matching and identification.
            ItemFields.Path,
            ItemFields.DateCreated,
            ItemFields.ProviderIds
        };

        if (config.MetadataSyncMetadata)
        {
            fields.Add(ItemFields.Overview);
            fields.Add(ItemFields.OriginalTitle);
            fields.Add(ItemFields.SortName);
            fields.Add(ItemFields.ProductionLocations);
            fields.Add(ItemFields.Taglines);
            fields.Add(ItemFields.Settings);
            fields.Add(ItemFields.CustomRating);
        }

        if (config.MetadataSyncGenres)
        {
            fields.Add(ItemFields.Genres);
        }

        if (config.MetadataSyncTags)
        {
            fields.Add(ItemFields.Tags);
        }

        if (config.MetadataSyncStudios)
        {
            fields.Add(ItemFields.Studios);
        }

        if (config.MetadataSyncPeople)
        {
            fields.Add(ItemFields.People);
        }

        return fields.ToArray();
    }

    /// <inheritdoc />
    protected override async Task<MetadataSyncItem?> BuildRecordAsync(
        MetadataWork source,
        IReadOnlyDictionary<(string SourceLibraryId, string SourceItemId), MetadataSyncItem> existing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Client == null)
        {
            return null;
        }

        var config = ConfigManager.Configuration;
        var fresh = await _metadataService.BuildRecordAsync(
            source.LibraryMapping,
            source.SourceItem,
            source.IsFolder,
            syncMetadata: config.MetadataSyncMetadata,
            syncImages: config.MetadataSyncImages,
            syncPeople: config.MetadataSyncPeople,
            syncStudios: config.MetadataSyncStudios,
            syncGenres: config.MetadataSyncGenres,
            syncTags: config.MetadataSyncTags,
            Client,
            cancellationToken).ConfigureAwait(false);

        if (fresh == null)
        {
            return null;
        }

        // Carry forward Synced* fields from the existing row so the hash
        // short-circuit (SourceHash == SyncedHash) can skip the deep JSON
        // compare on later refreshes. The service builds a fresh record with
        // null SyncedHash; the DB-side preserves SyncedHash via a CASE clause
        // in Upsert SQL, but the in-memory record is what HasChanges and
        // DecideStatus look at.
        var key = (fresh.SourceLibraryId, fresh.SourceItemId);
        if (existing.TryGetValue(key, out var prev))
        {
            fresh.Id = prev.Id;
            fresh.Status = prev.Status;
            fresh.LastSyncTime = prev.LastSyncTime;
            fresh.Reason = prev.Reason;

            fresh.Metadata.Synced = prev.Metadata.Synced;
            fresh.Metadata.SyncedHash = prev.Metadata.SyncedHash;
            fresh.Images.Synced = prev.Images.Synced;
            fresh.Images.SyncedHash = prev.Images.SyncedHash;
            fresh.People.Synced = prev.People.Synced;
            fresh.People.SyncedHash = prev.People.SyncedHash;
            fresh.Studios.Synced = prev.Studios.Synced;
            fresh.Studios.SyncedHash = prev.Studios.SyncedHash;
        }

        return fresh;
    }

    /// <inheritdoc />
    protected override (string SourceLibraryId, string SourceItemId) ExtractKey(MetadataSyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return (record.SourceLibraryId, record.SourceItemId);
    }

    // In scope when the row's library mapping is currently enabled. Disabling
    // a mapping preserves its metadata rows so the user's Ignored overrides
    // survive a toggle.
    /// <inheritdoc />
    protected override bool IsInScope(MetadataSyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        foreach (var mapping in ConfigManager.Configuration.GetEnabledLibraryMappings())
        {
            if (string.Equals(mapping.SourceLibraryId, record.SourceLibraryId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    protected override void RecordRunCompleted(Jellyfin.Plugin.ServerSync.Configuration.PluginConfiguration config, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.LastMetadataSyncTime = utcNow;
    }

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(10).Ticks
        }
    };
}
