using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.HistorySync;

/// <summary>
/// Scans History, starting from the items in each mapped local library.
/// User data is fetched only for source items whose translated path exists here, for each enabled user mapping and library mapping.
/// </summary>
public class RefreshHistorySyncTableTask
    : RefreshSyncTaskBase<HistorySyncItem, HistoryWork, (string SourceUserId, string SourceItemId)>
{
    private readonly HistorySyncTableService _historyService;
    private readonly ILibraryManager _libraryManager;
    private readonly ILinkRegistry _links;
    private readonly PerRunLookup<string, User?> _localUsers = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshHistorySyncTableTask"/> class.
    /// </summary>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="clientFactory">Builds the client for each source.</param>
    /// <param name="historyService">Builds and compares history sync rows.</param>
    /// <param name="libraryManager">Reads and changes Jellyfin's library.</param>
    /// <param name="links">Looks up which files are links.</param>
    /// <param name="manager">Reads and writes the sync table.</param>
    public RefreshHistorySyncTableTask(
        ILogger<RefreshHistorySyncTableTask> logger,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        HistorySyncTableService historyService,
        ILibraryManager libraryManager,
        ILinkRegistry links,
        HistorySyncTableManager manager)
        : base(logger, manager, clientFactory, configManager)
    {
        _historyService = historyService;
        _libraryManager = libraryManager;
        _links = links;
    }

    /// <inheritdoc />
    public override string Name => "Scan History";

    /// <inheritdoc />
    public override string Key => "ServerSyncRefreshHistoryTable";

    /// <inheritdoc />
    public override string Description => "Scans source and local servers for watch history differences and updates the history sync table.";

    /// <inheritdoc />
    public override string Category => "History Sync";

    /// <inheritdoc />
    protected override string ModuleMutexKey => "History";

    /// <summary>
    /// Forgets the local users found for the mappings, so a user added or removed between runs is seen.
    /// </summary>
    /// <inheritdoc />
    protected override void OnRunStarting() => _localUsers.Clear();

    /// <summary>
    /// A change to which parts of history are copied changes what every row merges to, so the next
    /// scan reads everything again.
    /// </summary>
    /// <inheritdoc />
    protected override string ScopeSettings
        => ((int)ConfigManager.Configuration.GetHistoryFields()).ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    protected override bool IsEnabled()
    {
        var config = ConfigManager.Configuration;
        if (!config.EnableHistorySync) return false;
        if (config.GetPullServers().Count == 0) return false;
        return config.GetEnabledUserMappings().Count > 0 && config.GetEnabledLibraryMappings().Count > 0;
    }

    /// <summary>
    /// Lists one server's history in three steps: map the local items of each library by path, find the source items whose translated path exists here, then fetch user data for those in batches.
    /// The second step makes one pass for each library, not one for each user.
    /// </summary>
    /// <inheritdoc />
    protected override async Task<IList<HistoryWork>> GetListAsync(ScanSource source, IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(progress);

        var client = source.Client;
        var userMappings = source.Server.GetEnabledUserMappings();
        var libraryMappings = source.Server.GetEnabledLibraryMappings();

        if (userMappings.Count == 0 || libraryMappings.Count == 0)
        {
            return Array.Empty<HistoryWork>();
        }

        progress.Report(2);

        var localPathsByLibrary = new Dictionary<string, Dictionary<string, BaseItem>>();
        foreach (var mapping in libraryMappings)
        {
            if (string.IsNullOrEmpty(mapping.LocalLibraryId)) continue;
            if (!Guid.TryParse(mapping.LocalLibraryId, out var localLibraryId)) continue;

            var paths = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
            if (_libraryManager.GetItemById(localLibraryId) is Folder root)
            {
                foreach (var item in root.GetRecursiveChildren().Where(child => child is not Folder && !string.IsNullOrEmpty(child.Path)))
                {
                    paths[item.Path] = item;
                }

                LinkPaths.AddAliases(paths, _links);
            }

            localPathsByLibrary[mapping.SourceLibraryId] = paths;
        }

        progress.Report(8);

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
                progress.Report(8 + Math.Min(21.0, 22.0 * fetchedSum / totalSum));
            }
        }

        var matchedIdsByLibrary = new ConcurrentDictionary<string, HashSet<Guid>>();
        await Parallel.ForEachAsync(
            libraryMappings,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (mapping, ct) =>
            {
                if (!Guid.TryParse(mapping.SourceLibraryId, out var sourceLibraryId)) return;
                if (!localPathsByLibrary.TryGetValue(mapping.SourceLibraryId, out var localPaths)) return;
                if (localPaths.Count == 0) return;

                var matched = new HashSet<Guid>();
                var leafTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video };
                const int pageSize = 1000;
                var startIndex = 0;
                var fetchedSoFar = 0L;
                var consecutiveErrors = 0;
                const int maxConsecutiveErrors = 3;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    BaseItemDtoQueryResult? page;
                    Exception? pageError = null;
                    try
                    {
                        page = await client.GetLibraryItemPathsAsync(sourceLibraryId, leafTypes, startIndex, pageSize, cancellationToken: ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        page = null;
                        pageError = ex;
                    }

                    // A missing page is a failed page, not the end of the library.
                    if (page is null)
                    {
                        Logger.LogWarning(pageError, "Discovery page failed for library {Library} at index {Index}", mapping.SourceLibraryName, startIndex);

                        // A source error means items may be missing, so the run skips pruning.
                        MarkSourceUnavailable($"source server unavailable during '{mapping.SourceLibraryName}' discovery");

                        if (++consecutiveErrors >= maxConsecutiveErrors)
                        {
                            break;
                        }

                        continue;
                    }

                    consecutiveErrors = 0;
                    if (page.Items == null || page.Items.Count == 0) break;

                    fetchedSoFar += page.Items.Count;
                    ReportDiscovery(mapping.SourceLibraryId, fetchedSoFar, page.TotalRecordCount ?? fetchedSoFar);

                    foreach (var item in page.Items)
                    {
                        if (!item.Id.HasValue || string.IsNullOrEmpty(item.Path)) continue;

                        if (mapping.FilterMode != LibraryFilterMode.AllowAll
                            && mapping.FilteredItems?.Count > 0
                            && PathUtilities.IsItemFiltered(item.Path, mapping.SourceRootPath, mapping.FilterMode, mapping.FilteredItems))
                        {
                            continue;
                        }

                        var localPath = PathUtilities.TranslatePath(item.Path, mapping.SourceRootPath, mapping.LocalRootPath);
                        if (localPaths.ContainsKey(localPath))
                        {
                            matched.Add(item.Id.Value);
                        }
                    }

                    if (page.TotalRecordCount.HasValue && startIndex + page.Items.Count >= page.TotalRecordCount.Value) break;
                    if (page.Items.Count < pageSize) break;
                    startIndex += page.Items.Count;
                }

                matchedIdsByLibrary[mapping.SourceLibraryId] = matched;
            }).ConfigureAwait(false);

        var totalMatched = matchedIdsByLibrary.Values.Sum(s => s.Count);
        Logger.LogInformation(
            "{Task}: discovery matched {Total} items across {Libraries} libraries. Will fetch user-data for {Pairs} (user × library) pairs",
            Name,
            totalMatched,
            libraryMappings.Count,
            userMappings.Count * libraryMappings.Count);

        progress.Report(30);

        if (totalMatched == 0)
        {
            return Array.Empty<HistoryWork>();
        }

        var work = new ConcurrentBag<HistoryWork>();
        var pairsTotal = Math.Max(userMappings.Count * libraryMappings.Count, 1);
        var pairsDone = 0;

        foreach (var userMapping in userMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(userMapping.SourceUserId, out var sourceUserId))
            {
                Logger.LogWarning("Skipping user mapping with invalid source ID: {Id}", userMapping.SourceUserId);
                continue;
            }

            foreach (var libraryMapping in libraryMappings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!matchedIdsByLibrary.TryGetValue(libraryMapping.SourceLibraryId, out var matchedIds) || matchedIds.Count == 0)
                {
                    pairsDone++;
                    progress.Report(Math.Min(100, 30 + (70.0 * pairsDone / pairsTotal)));
                    continue;
                }

                var ids = matchedIds.ToArray();
                const int batchSize = 50;
                var batchesForPair = (ids.Length + batchSize - 1) / batchSize;
                var batchesDone = 0;
                for (var i = 0; i < ids.Length; i += batchSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = ids.Skip(i).Take(batchSize).Cast<Guid?>().ToArray();
                    var page = await client.GetItemsWithUserDataByIdsAsync(sourceUserId, chunk, cancellationToken).ConfigureAwait(false);

                    batchesDone++;
                    progress.Report(Math.Min(100, 30 + (70.0 * (pairsDone + ((double)batchesDone / batchesForPair)) / pairsTotal)));

                    if (page?.Items == null)
                    {
                        // A failed batch leaves items unseen, so the run skips pruning.
                        MarkSourceUnavailable($"source server unavailable fetching user-data for '{libraryMapping.SourceLibraryName}'");
                        continue;
                    }

                    foreach (var item in page.Items)
                    {
                        work.Add(new HistoryWork(source, userMapping, libraryMapping, item));
                    }
                }

                pairsDone++;
                progress.Report(Math.Min(100, 30 + (70.0 * pairsDone / pairsTotal)));
            }
        }

        progress.Report(100);
        return work.ToList();
    }

    /// <inheritdoc />
    protected override Task<HistorySyncItem?> BuildRecordAsync(
        HistoryWork source,
        IReadOnlyDictionary<(string SourceUserId, string SourceItemId), HistorySyncItem> existing,
        CancellationToken cancellationToken)
    {
        var sourceItemId = source.SourceItem.Id!.Value.ToString("N", CultureInfo.InvariantCulture);
        var record = _historyService.BuildRecord(
            source.UserMapping,
            source.LibraryMapping,
            source.SourceItem,
            sourceItemId,
            negotiateWithSource: source.Source.Server.Mode == ServerMode.Sync && source.LibraryMapping.NegotiateHistory,
            findUser: mapping => _localUsers.Get(mapping.LocalUserId, _ => _historyService.FindLocalUser(mapping)));

        if (record != null)
        {
            record.ServerKey = source.Source.Key;

            // Without the synced baseline carried over, every scan would discard it.
            if (existing.TryGetValue((record.SourceUserId, record.SourceItemId), out var prev))
            {
                CarryForwardRowState(record, prev);
                record.SourceState.Synced = prev.SourceState.Synced;
                record.SourceState.SyncedHash = prev.SourceState.SyncedHash;

                // The restored base makes the merge three way, so a local change survives.
                record.CarryNegotiatedBaseFrom(prev);
                if (record.HasNegotiatedBase)
                {
                    HistorySyncMergeService.MergeHistoryData(record);
                }
            }
        }

        return Task.FromResult(record);
    }

    /// <inheritdoc />
    protected override (string SourceUserId, string SourceItemId) ExtractKey(HistorySyncItem record)
        => (record.SourceUserId, record.SourceItemId);

    /// <summary>
    /// Two servers collide when they hold history for the same local user and local file.
    /// </summary>
    /// <inheritdoc />
    protected override string? PriorityKeyOf(HistoryWork source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var path = source.SourceItem.Path;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var localPath = PathUtilities.TranslatePath(path, source.LibraryMapping.SourceRootPath, source.LibraryMapping.LocalRootPath);
        return source.UserMapping.LocalUserId + "|" + localPath;
    }

    /// <inheritdoc />
    protected override string? PriorityKeyOf(HistorySyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return string.IsNullOrEmpty(record.LocalPath) ? null : record.LocalUserId + "|" + record.LocalPath;
    }

    /// <summary>
    /// Picks the server that speaks for a file this run: the highest ranked one whose history of it has moved since this server last looked, and by rank when none has.
    /// History is merged, not replaced, so a server passed over is heard on a later run.
    /// </summary>
    /// <inheritdoc />
    protected override Task<IReadOnlyDictionary<string, HistoryWork>> ContestAsync(IReadOnlyDictionary<string, IReadOnlyList<HistoryWork>> contested, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contested);
        var winners = new Dictionary<string, HistoryWork>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, offers) in contested)
        {
            var moved = offers.FirstOrDefault(HasMovedSinceSeen);
            if (moved is not null && !ReferenceEquals(moved, offers[0]))
            {
                winners[key] = moved;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, HistoryWork>>(winners);
    }

    /// <summary>
    /// On a run that lists only what changed, a server lists a file because its history of it moved.
    /// </summary>
    /// <inheritdoc />
    protected override bool MayOutrankOwner(HistorySyncItem record) => true;

    /// <summary>Says whether a server's history of a file differs from what this server last saw of it.</summary>
    private bool HasMovedSinceSeen(HistoryWork offer)
    {
        if (offer.SourceItem.Id is not { } id
            || !ExistingRows.TryGetValue((offer.UserMapping.SourceUserId, id.ToString("N", CultureInfo.InvariantCulture)), out var seen))
        {
            return true;
        }

        var now = offer.SourceItem.UserData;
        return seen.SourceIsPlayed != now?.Played
            || seen.SourcePlayCount != now?.PlayCount
            || seen.SourcePlaybackPositionTicks != now?.PlaybackPositionTicks
            || seen.SourceIsFavorite != now?.IsFavorite
            || !HistorySyncMergeService.SameInstantToSecond(seen.SourceLastPlayedDate, now?.LastPlayedDate?.UtcDateTime);
    }

    /// <summary>
    /// Keeps a history row when another server speaks for its file.
    /// The row records what was last seen of its own server, and a negotiated base belongs to that pair of servers alone.
    /// </summary>
    /// <param name="record">The row another server covers.</param>
    /// <returns>Always <c>false</c>.</returns>
    protected override bool CanRetire(HistorySyncItem record) => false;

    /// <summary>
    /// Says whether both the row's library mapping and its user mapping are on.
    /// Turning either off keeps the history rows, so Ignored rows survive it.
    /// </summary>
    /// <inheritdoc />
    protected override bool IsInScope(HistorySyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!IsEnabledSourceLibrary(record.SourceLibraryId))
        {
            return false;
        }

        foreach (var mapping in ConfigManager.Configuration.GetEnabledUserMappings())
        {
            if (string.Equals(mapping.SourceUserId, record.SourceUserId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(mapping.LocalUserId, record.LocalUserId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }


    /// <inheritdoc />
    protected override void RecordRunCompleted(PluginConfiguration config, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.LastHistorySyncTime = utcNow;
    }
}
