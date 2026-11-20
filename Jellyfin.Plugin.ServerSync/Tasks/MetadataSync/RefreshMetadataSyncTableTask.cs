using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.MetadataSync;

/// <summary>
/// Scans Metadata: walks every enabled library, fetches its items and, when folder items are on, its folders, and builds a <see cref="MetadataSyncItem"/> for each.
/// </summary>
public class RefreshMetadataSyncTableTask
    : RefreshSyncTaskBase<MetadataSyncItem, MetadataWork, (string SourceLibraryId, string SourceItemId)>
{
    private readonly MetadataSyncTableService _metadataService;
    private readonly ScanConflictSettler _conflicts;
    private readonly MediaSegmentStore? _segments;
    private readonly string _thisServerId;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshMetadataSyncTableTask"/> class.
    /// </summary>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="clientFactory">Builds the client for each source.</param>
    /// <param name="metadataService">Builds and compares metadata sync rows.</param>
    /// <param name="manager">Reads and writes the sync table.</param>
    /// <param name="conflicts">Settles which server speaks for an item both sides changed.</param>
    /// <param name="watermarks">Holds when each source was last scanned, or null when scans are never incremental.</param>
    /// <param name="statistics">Keeps what each scan found for the Statistics tab, or null to keep nothing.</param>
    /// <param name="segments">Holds the local segment table, which each row's media segments are compared with, or null to read no segments.</param>
    /// <param name="applicationHost">Gives this server's id, so its own segments are never taken back, or null.</param>
    public RefreshMetadataSyncTableTask(
        ILogger<RefreshMetadataSyncTableTask> logger,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        MetadataSyncTableService metadataService,
        MetadataSyncTableManager manager,
        ScanConflictSettler conflicts,
        ScanWatermarkStore watermarks,
        SyncStatisticsStore? statistics = null,
        MediaSegmentStore? segments = null,
        MediaBrowser.Controller.IServerApplicationHost? applicationHost = null)
        : base(logger, manager, clientFactory, configManager, watermarks, statistics)
    {
        _metadataService = metadataService;
        _conflicts = conflicts;
        _segments = segments;
        _thisServerId = applicationHost?.SystemId ?? string.Empty;
    }

    /// <summary>
    /// What each server holding a file answered for its media segments in this run, by local path, with the server's rank.
    /// <see cref="GetListAsync"/> fills it and <see cref="BuildRecordAsync"/> reads it.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentBag<(int Rank, string ServerKey, string? ServerId, string SourceItemId, Models.Peer.PeerSegmentsItem Answer)>> _segmentAnswers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Says whether rows carry media segments, which needs the store and Enable media segment provider on.</summary>
    private bool ReadsSegments => _segments is not null && ConfigManager.Configuration.MetadataSyncSegmentProvider;

    /// <summary>
    /// The local items of each mapped library by path, for one run and by source library id.
    /// <see cref="GetListAsync"/> fills it and <see cref="BuildRecordAsync"/> reads it, so the parallel build does not ask Jellyfin's library database for every item.
    /// </summary>
    private readonly ConcurrentDictionary<string, (Dictionary<string, BaseItem> Leaves, Dictionary<string, BaseItem> Folders)> _localByLibrary = new(StringComparer.Ordinal);

    /// <summary>
    /// Clears the local items a run holds, which each server's listing then fills again.
    /// </summary>
    /// <inheritdoc />
    protected override void OnRunStarting()
    {
        _localByLibrary.Clear();
        _segmentAnswers.Clear();
    }

    /// <summary>
    /// Clears the local items a scan held, so a single item from a hint is read from the library as it is now.
    /// </summary>
    /// <inheritdoc />
    protected override void PrepareForOne(MetadataWork work)
    {
        _localByLibrary.Clear();
        _segmentAnswers.Clear();
    }

    /// <summary>
    /// Lets go of the local items and segment answers once the scan has built its rows, so they are not held between runs.
    /// </summary>
    /// <inheritdoc />
    protected override async Task FinalizeAsync(CancellationToken cancellationToken)
    {
        _localByLibrary.Clear();
        _segmentAnswers.Clear();

        // Segments kept for files the Metadata table no longer holds are forgotten. A failure never fails the scan.
        if (ReadsSegments)
        {
            try
            {
                _segments!.RemoveUntracked();
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                Logger.LogWarning(ex, "{Task}: could not forget the media segments of files no longer tracked", Name);
            }
        }

        await base.FinalizeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Finds a local item among the ones this run listed, or null when the run did not list it.</summary>
    private BaseItem? KnownLocalItem(string sourceLibraryId, string path, bool isFolder)
        => _localByLibrary.TryGetValue(sourceLibraryId, out var local) && (isFolder ? local.Folders : local.Leaves).TryGetValue(path, out var item) ? item : null;

    /// <inheritdoc />
    protected override bool SupportsIncremental => true;

    /// <inheritdoc />
    protected override string ScopeSettings
    {
        get
        {
            var config = ConfigManager.Configuration;
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                config.MetadataSyncMetadata,
                config.MetadataSyncGenres,
                config.MetadataSyncTags,
                config.MetadataSyncStudios,
                config.MetadataSyncPeople,
                config.MetadataSyncImages,
                config.MetadataSyncFolderItems,
                config.MetadataSyncSegmentProvider,
                config.MetadataSyncConsolidateIntros,
                config.MetadataSyncConsolidateOutros,
                config.MetadataSyncConsolidateOtherSegments
            });
        }
    }

    /// <summary>
    /// Settles conflicts against a peer that keeps stamps: a queued row whose local value was edited here more recently is kept, and the peer is told to pull it.
    /// </summary>
    /// <inheritdoc />
    protected override Task ResolveConflictsAsync(IList<MetadataSyncItem> queued, CancellationToken cancellationToken)
        => _conflicts.SettleAsync(
            queued,
            Sources,
            Models.Queue.HintKind.Metadata,
            record => record.LocalItemId ?? string.Empty,
            record => record.SourceItemId,
            record => (record.LocalItemId, record.LocalPath),
            record => Manager.Upsert(record),
            Logger,
            cancellationToken);

    private HashSet<string> _versionedServers = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    protected override async Task PrepareContestAsync(CancellationToken cancellationToken)
        => _versionedServers = ConfigManager.Configuration.MetadataSyncPrecedence == Models.Configuration.SyncPrecedence.Rank
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : await _conflicts.ServersWithVersionsAsync(Sources, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Among the servers offering one item, the one that holds the newest recorded edit of it speaks.
    /// </summary>
    /// <inheritdoc />
    protected override Task<IReadOnlyDictionary<string, MetadataWork>> ContestAsync(IReadOnlyDictionary<string, IReadOnlyList<MetadataWork>> contested, CancellationToken cancellationToken)
        => _conflicts.NewestOffersAsync(
            contested,
            Models.Queue.HintKind.Metadata,
            _versionedServers,
            work => work.Source,
            work => work.SourceItem.Id?.ToString("N", CultureInfo.InvariantCulture),
            cancellationToken);

    /// <summary>
    /// Lets a change on a lower ranked server that keeps stamps be weighed against this server's value by ResolveConflictsAsync, on a run that lists only what changed.
    /// </summary>
    /// <inheritdoc />
    protected override bool MayOutrankOwner(MetadataSyncItem record)
        => record?.ServerKey is { } key && _versionedServers.Contains(key);

    /// <inheritdoc />
    public override string Name => "Scan Metadata";

    /// <inheritdoc />
    public override string Key => "ServerSyncRefreshMetadataTable";

    /// <inheritdoc />
    public override string Description => "Scans source and local servers for metadata differences and updates the metadata sync table.";

    /// <inheritdoc />
    public override string Category => "Metadata Sync";

    /// <inheritdoc />
    protected override string ModuleMutexKey => "Metadata";

    /// <summary>
    /// Gets how many rows are built at once, which is in effect how much CPU a scan may use, since the build is mostly CPU bound.
    /// Lower it to leave room for playback and transcoding.
    /// </summary>
    /// <inheritdoc />
    protected override int BuildRecordParallelism => Math.Clamp(ConfigManager.Configuration.RefreshParallelism, 1, 16);

    /// <inheritdoc />
    protected override bool IsEnabled()
    {
        var config = ConfigManager.Configuration;
        if (!config.EnableMetadataSync) return false;
        if (config.GetPullServers().Count == 0) return false;
        if (config.GetEnabledLibraryMappings().Count == 0) return false;
        return config.MetadataSyncMetadata || config.MetadataSyncImages
            || config.MetadataSyncPeople || config.MetadataSyncStudios || config.MetadataSyncSegmentProvider;
    }

    /// <summary>
    /// Lists one server's items with a light pass and then a heavy one: page every library for paths and ids only, keep the items whose translated path exists here, then fetch full metadata for those in batches.
    /// Full metadata is never fetched for source items this server does not hold.
    /// </summary>
    /// <inheritdoc />
    protected override async Task<IList<MetadataWork>> GetListAsync(ScanSource source, IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(progress);

        var config = ConfigManager.Configuration;
        var client = source.Client;
        var enabledMappings = source.Server.GetEnabledLibraryMappings();
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

        var localByLibrary = new Dictionary<string, (Dictionary<string, BaseItem> Leaves, Dictionary<string, BaseItem> Folders)>();
        foreach (var mapping in enabledMappings)
        {
            if (string.IsNullOrEmpty(mapping.LocalLibraryId)) continue;
            if (!Guid.TryParse(mapping.LocalLibraryId, out var localLibraryGuid)) continue;
            localByLibrary[mapping.SourceLibraryId] = _metadataService.GetLocalItemsByPath(localLibraryGuid);
            _localByLibrary[mapping.SourceLibraryId] = localByLibrary[mapping.SourceLibraryId];
        }

        progress.Report(8);

        var matchedLeavesByMapping = new ConcurrentDictionary<string, ConcurrentBag<Guid>>();
        var matchedFoldersByMapping = new ConcurrentDictionary<string, ConcurrentBag<Guid>>();

        // A full scan fetches the cast only where it may have changed, since building it is most of the source's work.
        var castSince = config.MetadataSyncPeople && !IsIncrementalRun ? LastListedSince(source) : null;
        var savedSinceByMapping = new ConcurrentDictionary<string, ConcurrentBag<Guid>>();

        var discoveryProgress = new ConcurrentDictionary<string, (long Fetched, long Total)>(StringComparer.Ordinal);
        void ReportDiscovery(string key, long fetched, long total)
        {
            discoveryProgress[key] = (fetched, total);
            long fetchedSum = 0, totalSum = 0;
            foreach (var v in discoveryProgress.Values)
            {
                fetchedSum += v.Fetched;
                totalSum += v.Total;
            }

            if (totalSum > 0)
            {
                progress.Report(8 + Math.Min(31.0, 32.0 * fetchedSum / totalSum));
            }
        }

        await Parallel.ForEachAsync(
            enabledMappings,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (mapping, ct) =>
            {
                if (!Guid.TryParse(mapping.SourceLibraryId, out var sourceLibraryId)) return;
                if (!localByLibrary.TryGetValue(mapping.SourceLibraryId, out var localPaths)) return;

                var leafBag = matchedLeavesByMapping.GetOrAdd(mapping.SourceLibraryId, _ => new ConcurrentBag<Guid>());
                var folderBag = matchedFoldersByMapping.GetOrAdd(mapping.SourceLibraryId, _ => new ConcurrentBag<Guid>());

                await DiscoverForRunAsync(source, mapping, sourceLibraryId, leafTypes, localPaths.Leaves, leafBag, mapping.SourceLibraryId + "|leaves", ReportDiscovery, ct).ConfigureAwait(false);
                if (includeFolderItems)
                {
                    await DiscoverForRunAsync(source, mapping, sourceLibraryId, folderTypes, localPaths.Folders, folderBag, mapping.SourceLibraryId + "|folders", ReportDiscovery, ct).ConfigureAwait(false);
                }

                if (castSince is not null)
                {
                    var savedBag = savedSinceByMapping.GetOrAdd(mapping.SourceLibraryId, _ => new ConcurrentBag<Guid>());
                    await DiscoverMatchingIdsAsync(source.Client, mapping, sourceLibraryId, leafTypes, localPaths.Leaves, savedBag, mapping.SourceLibraryId + "|leaves|saved", ReportDiscovery, castSince, ct).ConfigureAwait(false);
                    if (includeFolderItems)
                    {
                        await DiscoverMatchingIdsAsync(source.Client, mapping, sourceLibraryId, folderTypes, localPaths.Folders, savedBag, mapping.SourceLibraryId + "|folders|saved", ReportDiscovery, castSince, ct).ConfigureAwait(false);
                    }
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

        var work = new ConcurrentBag<MetadataWork>();
        var fetched = 0;
        var heavyDenominator = Math.Max(totalMatched, 1);

        // Fifty ids keep the URL under common reverse proxy header limits.
        const int heavyBatchSize = 50;
        var chunksPerMappingParallelism = BuildRecordParallelism;
        var fieldsWithoutCast = fields.Where(f => f != ItemFields.People).ToArray();
        var castFetches = 0;

        async Task FetchHeavyAsync(LibraryMapping mapping, IReadOnlyList<Guid> allIds, bool isFolder, CancellationToken ct)
        {
            if (allIds.Count == 0) return;

            savedSinceByMapping.TryGetValue(mapping.SourceLibraryId, out var savedBag);
            var saved = savedBag is null ? null : new HashSet<Guid>(savedBag);
            var withCast = new List<Guid>();
            var withoutCast = new List<Guid>();
            foreach (var id in allIds)
            {
                (NeedsCast(mapping, id, saved) ? withCast : withoutCast).Add(id);
            }

            Interlocked.Add(ref castFetches, withCast.Count);
            await FetchBatchesAsync(mapping, withCast, isFolder, fields, true, ct).ConfigureAwait(false);
            await FetchBatchesAsync(mapping, withoutCast, isFolder, fieldsWithoutCast, false, ct).ConfigureAwait(false);
        }

        bool NeedsCast(LibraryMapping mapping, Guid id, HashSet<Guid>? saved)
            => CastNeeded(castSince is null ? null : saved, id, ExistingRows.GetValueOrDefault((mapping.SourceLibraryId, id.ToString("N", CultureInfo.InvariantCulture))));

        async Task FetchBatchesAsync(LibraryMapping mapping, IReadOnlyList<Guid> ids, bool isFolder, ItemFields[] requested, bool castFetched, CancellationToken ct)
        {
            if (ids.Count == 0) return;

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
                    var items = await client.GetItemsByIdsAsync(chunk, requested, batchSize: heavyBatchSize, cancellationToken: innerCt).ConfigureAwait(false);
                    foreach (var item in items)
                    {
                        work.Add(new MetadataWork(source, mapping, item, isFolder, castFetched));
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

        if (castSince is not null)
        {
            Logger.LogInformation("{Task}: read the cast of {Count} of {Total} items from '{Server}', since the rest are unchanged since the last scan", Name, castFetches, totalMatched, source.Name);
        }

        var listed = work.ToList();
        if (ReadsSegments)
        {
            await ReadSegmentsAsync(source, listed, cancellationToken).ConfigureAwait(false);
        }

        progress.Report(100);
        return listed;
    }

    /// <summary>
    /// Reads the media segments of the files a source listed, 200 to a request, and notes each answer under the file's local path.
    /// A source that cannot answer notes nothing, so its files keep the segments their rows held.
    /// </summary>
    private async Task ReadSegmentsAsync(ScanSource source, IReadOnlyList<MetadataWork> listed, CancellationToken cancellationToken)
    {
        var files = listed
            .Where(w => !w.IsFolder && w.SourceItem.Id is not null && PriorityKeyOf(w) is { Length: > 0 })
            .GroupBy(w => w.SourceItem.Id!.Value.ToString("N", CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(w => PriorityKeyOf(w)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var batch in files.Keys.Chunk(Models.Peer.PeerSegmentsRequest.MaxItems))
        {
            Models.Peer.PeerSegmentsResponse answer;
            try
            {
                answer = await source.Client.GetPeerSegmentsAsync(batch, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Logger.LogWarning("{Task}: could not read media segments from '{Server}', so its files keep the ones their rows held: {Reason}", Name, source.Name, JPKribs.Jellyfin.Base.FormatUtilities.SanitizeForLog(ex.Message, 300));
                return;
            }

            foreach (var item in answer.Items)
            {
                if (item is null || !files.TryGetValue(item.ItemId ?? string.Empty, out var paths))
                {
                    continue;
                }

                foreach (var path in paths)
                {
                    _segmentAnswers.GetOrAdd(path, _ => new()).Add((source.Priority, source.Key, source.Server.ServerId, item.ItemId!, item));
                }
            }
        }
    }

    /// <summary>
    /// Pages the source library for item paths and adds the ids whose translated path exists in <paramref name="localPaths"/> to the shared bag.
    /// On a run that lists only what changed, it keeps the changed ids and the ids with no row yet.
    /// </summary>
    private async Task DiscoverForRunAsync(
        ScanSource source,
        LibraryMapping mapping,
        Guid sourceLibraryId,
        BaseItemKind[] includeTypes,
        Dictionary<string, BaseItem> localPaths,
        ConcurrentBag<Guid> matchedIds,
        string progressKey,
        Action<string, long, long> reportDiscovery,
        CancellationToken cancellationToken)
    {
        var changedSince = ChangedSince(source);
        if (changedSince is null)
        {
            await DiscoverMatchingIdsAsync(source.Client, mapping, sourceLibraryId, includeTypes, localPaths, matchedIds, progressKey, reportDiscovery, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var all = new ConcurrentBag<Guid>();
        var changed = new ConcurrentBag<Guid>();
        await DiscoverMatchingIdsAsync(source.Client, mapping, sourceLibraryId, includeTypes, localPaths, all, progressKey, reportDiscovery, null, cancellationToken).ConfigureAwait(false);
        await DiscoverMatchingIdsAsync(source.Client, mapping, sourceLibraryId, includeTypes, localPaths, changed, progressKey + "|changed", reportDiscovery, changedSince, cancellationToken).ConfigureAwait(false);
        var changedIds = new HashSet<Guid>(changed);
        foreach (var id in new HashSet<Guid>(all).Where(candidate => changedIds.Contains(candidate) || !ExistingRows.ContainsKey((mapping.SourceLibraryId, candidate.ToString("N", CultureInfo.InvariantCulture)))))
        {
            matchedIds.Add(id);
        }
    }

    /// <summary>Pages one source library for paths and collects the ids that match a local path.</summary>
    private async Task DiscoverMatchingIdsAsync(
        SourceServerClient client,
        LibraryMapping mapping,
        Guid sourceLibraryId,
        BaseItemKind[] includeTypes,
        Dictionary<string, BaseItem> localPaths,
        ConcurrentBag<Guid> matchedIds,
        string progressKey,
        Action<string, long, long> reportDiscovery,
        DateTime? changedSince,
        CancellationToken cancellationToken)
    {
        if (localPaths.Count == 0) return;

        const int pageSize = 1000;
        var startIndex = 0;
        var fetchedSoFar = 0L;
        var consecutiveErrors = 0;
        const int maxConsecutiveErrors = 3;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            BaseItemDtoQueryResult? page;
            Exception? pageError = null;
            try
            {
                page = await client.GetLibraryItemPathsAsync(sourceLibraryId, includeTypes, startIndex, pageSize, changedSince, cancellationToken).ConfigureAwait(false);
            }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                page = null;
                pageError = ex;
            }

            // A missing page is a failed page, not the end of the library.
            if (page is null)
            {
                Logger.LogWarning(pageError, "Discovery page failed for library {Library} at index {Index}", mapping.SourceLibraryName, startIndex);

                // A source error skips the prune, and a few more pages are still tried.
                MarkSourceUnavailable($"source server unavailable during '{mapping.SourceLibraryName}' discovery");

                if (++consecutiveErrors >= maxConsecutiveErrors)
                {
                    return;
                }

                continue;
            }

            consecutiveErrors = 0;

            if (page.Items == null || page.Items.Count == 0)
            {
                return;
            }

            fetchedSoFar += page.Items.Count;
            reportDiscovery(progressKey, fetchedSoFar, page.TotalRecordCount ?? fetchedSoFar);

            foreach (var item in page.Items)
            {
                if (!item.Id.HasValue || string.IsNullOrEmpty(item.Path))
                {
                    continue;
                }

                if (mapping.FilterMode != LibraryFilterMode.AllowAll
                    && mapping.FilteredItems?.Count > 0
                    && PathUtilities.IsItemFiltered(item.Path, mapping.SourceRootPath, mapping.FilterMode, mapping.FilteredItems))
                {
                    continue;
                }

                // A higher ranked server already described this file, so it is not fetched again.
                var localPath = PathUtilities.TranslatePath(item.Path, mapping.SourceRootPath, mapping.LocalRootPath);
                if (localPaths.ContainsKey(localPath) && !ClaimedByHigherServer(localPath))
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

    /// <summary>Says whether an item type is one Metadata treats as a folder.</summary>
    /// <param name="type">The item type the source reports.</param>
    /// <returns><c>true</c> for series, seasons, albums, artists, and box sets.</returns>
    public static bool IsFolderType(BaseItemDto_Type? type) => type is BaseItemDto_Type.Series or BaseItemDto_Type.Season or BaseItemDto_Type.MusicAlbum or BaseItemDto_Type.MusicArtist or BaseItemDto_Type.BoxSet;

    /// <summary>
    /// Builds the smallest <see cref="ItemFields"/> set that covers the metadata categories that are on.
    /// Leaving out unused fields, <see cref="ItemFields.People"/> above all, makes each page much smaller.
    /// </summary>
    /// <param name="config">The plugin settings that say which categories are on.</param>
    /// <returns>The fields to request from the source.</returns>
    public static ItemFields[] BuildRequestedFields(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var fields = new List<ItemFields>
        {
            // Path matching and identification always need these.
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

    /// <summary>
    /// Says whether a scan reads an item's cast from the source. It does for a new item, one the source saved since the last scan, one whose stored cast is missing, and every item when nothing is known about what the source saved.
    /// </summary>
    /// <param name="savedSinceLastScan">The ids the source saved since the last clean scan, or null when that is not known.</param>
    /// <param name="id">The source item.</param>
    /// <param name="prior">The row the last scan stored for the item, or null.</param>
    /// <returns><c>true</c> to read the cast.</returns>
    public static bool CastNeeded(IReadOnlySet<Guid>? savedSinceLastScan, Guid id, MetadataSyncItem? prior)
        => savedSinceLastScan is null || savedSinceLastScan.Contains(id) || prior?.People.Source is null;

    /// <summary>Says whether the source changed anything besides the cast since the prior row was built.</summary>
    /// <param name="fresh">The row this scan built.</param>
    /// <param name="prior">The row the last scan stored.</param>
    /// <returns><c>true</c> when metadata, images, or studios read differently.</returns>
    public static bool SourceChangedBesidesCast(MetadataSyncItem fresh, MetadataSyncItem prior)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        ArgumentNullException.ThrowIfNull(prior);
        return !string.Equals(fresh.Metadata.SourceHash, prior.Metadata.SourceHash, StringComparison.Ordinal)
            || !string.Equals(fresh.Images.SourceHash, prior.Images.SourceHash, StringComparison.Ordinal)
            || !string.Equals(fresh.Studios.SourceHash, prior.Studios.SourceHash, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    protected override async Task<MetadataSyncItem?> BuildRecordAsync(
        MetadataWork source,
        IReadOnlyDictionary<(string SourceLibraryId, string SourceItemId), MetadataSyncItem> existing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var config = ConfigManager.Configuration;

        // The prior row's image manifest lets unchanged images skip their HTTP calls.
        var sourceItemId = source.SourceItem.Id!.Value.ToString("N", CultureInfo.InvariantCulture);
        existing.TryGetValue((source.LibraryMapping.SourceLibraryId, sourceItemId), out var priorRecord);

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
            priorSourceImagesJson: priorRecord?.Images.Source,
            priorLocalImagesJson: priorRecord?.Images.Local,
            deepImageVerification: config.DeepImageVerification,
            source.Source.Client,
            cancellationToken,
            (path, isFolder) => KnownLocalItem(source.LibraryMapping.SourceLibraryId, path, isFolder),
            source.CastFetched ? null : priorRecord?.People.Source).ConfigureAwait(false);

        if (fresh == null)
        {
            return null;
        }

        // Other metadata changed without a save the listing caught, so the cast may have changed with it.
        if (!source.CastFetched && priorRecord is not null && SourceChangedBesidesCast(fresh, priorRecord))
        {
            var full = (await source.Source.Client.GetItemsByIdsAsync(new[] { source.SourceItem.Id!.Value }, BuildRequestedFields(config), batchSize: 1, cancellationToken: cancellationToken).ConfigureAwait(false)).FirstOrDefault();
            if (full is not null)
            {
                return await BuildRecordAsync(source with { SourceItem = full, CastFetched = true }, existing, cancellationToken).ConfigureAwait(false);
            }
        }

        fresh.ServerKey = source.Source.Key;
        if (ReadsSegments && !fresh.IsFolder && !string.IsNullOrEmpty(fresh.LocalItemId))
        {
            FillSegments(fresh, priorRecord);
        }

        // Without the synced values carried over, every scan would discard the baseline.
        var key = (fresh.SourceLibraryId, fresh.SourceItemId);
        if (existing.TryGetValue(key, out var prev))
        {
            CarryForwardRowState(fresh, prev);

            fresh.Metadata.Synced = prev.Metadata.Synced;
            fresh.Metadata.SyncedHash = prev.Metadata.SyncedHash;
            fresh.Images.Synced = prev.Images.Synced;
            fresh.Images.SyncedHash = prev.Images.SyncedHash;
            fresh.People.Synced = prev.People.Synced;
            fresh.People.SyncedHash = prev.People.SyncedHash;
            fresh.Studios.Synced = prev.Studios.Synced;
            fresh.Studios.SyncedHash = prev.Studios.SyncedHash;
            fresh.Segments.Synced = prev.Segments.Synced;
            fresh.Segments.SyncedHash = prev.Segments.SyncedHash;
        }

        return fresh;
    }

    /// <summary>
    /// Sets a row's media segments: the source value is what the pool holds for the file, picked across every server that answered for it in rank order,
    /// and the local value is what the local segment table holds. With no answer for the file this run, the row keeps the source value it held.
    /// A pool with no segments for a file that has none kept here is no change, unless Remove segments no server has is on.
    /// </summary>
    private void FillSegments(MetadataSyncItem fresh, MetadataSyncItem? prior)
    {
        var local = MediaSegmentsValue.FromRow(_segments!.Get(fresh.LocalItemId!));
        if (_segmentAnswers.TryGetValue(fresh.LocalPath ?? string.Empty, out var answers))
        {
            var config = ConfigManager.Configuration;
            var chosen = MediaSegmentsValue.Choose(answers.OrderBy(a => a.Rank).Select(a => (a.ServerKey, a.ServerId, a.SourceItemId, a.Answer)), _thisServerId)
                ?.Consolidate(config.MetadataSyncConsolidateIntros, config.MetadataSyncConsolidateOutros, config.MetadataSyncConsolidateOtherSegments);
            fresh.Segments.UpdateSource(MediaSegmentsValue.Worth(chosen, local, config.MetadataSyncRemoveSegments)?.ToJson());
        }
        else
        {
            fresh.Segments.UpdateSource(prior?.Segments.Source);
        }

        fresh.Segments.Local = local?.ToJson();
    }

    /// <inheritdoc />
    protected override (string SourceLibraryId, string SourceItemId) ExtractKey(MetadataSyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return (record.SourceLibraryId, record.SourceItemId);
    }

    /// <summary>
    /// Two servers collide when they describe the same local file.
    /// </summary>
    /// <inheritdoc />
    protected override string? PriorityKeyOf(MetadataWork source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var path = source.SourceItem.Path;
        return string.IsNullOrEmpty(path) ? null : PathUtilities.TranslatePath(path, source.LibraryMapping.SourceRootPath, source.LibraryMapping.LocalRootPath);
    }

    /// <inheritdoc />
    protected override string? PriorityKeyOf(MetadataSyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return string.IsNullOrEmpty(record.LocalPath) ? null : record.LocalPath;
    }

    /// <summary>
    /// Says whether the row's library mapping is on.
    /// Turning a mapping off keeps its metadata rows, so Ignored rows survive it.
    /// </summary>
    /// <inheritdoc />
    protected override bool IsInScope(MetadataSyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return IsEnabledSourceLibrary(record.SourceLibraryId);
    }

    /// <inheritdoc />
    protected override void RecordRunCompleted(PluginConfiguration config, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.LastMetadataSyncTime = utcNow;
    }
}
