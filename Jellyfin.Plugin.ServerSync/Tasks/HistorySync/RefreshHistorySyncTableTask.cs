using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using TaskTriggerInfo = MediaBrowser.Model.Tasks.TaskTriggerInfo;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Source-side work item for the History refresh task: a (user mapping,
/// library mapping, source item) triple that
/// <see cref="RefreshHistorySyncTableTask.BuildRecordAsync"/> turns into a
/// <see cref="HistorySyncItem"/>.
/// </summary>
public sealed record HistoryWork(UserMapping UserMapping, LibraryMapping LibraryMapping, BaseItemDto SourceItem);

/// <summary>
/// Refresh phase for History sync. Local-first discovery: enumerate the
/// items in each mapped local library, then for each (enabled user mapping
/// × enabled library mapping) batch-fetch user-data for the source items
/// whose translated path exists locally. We never fetch user-data for items
/// that aren't in the local library — the previous bulk per-(user × library)
/// approach pulled tens of thousands of records over the wire just to
/// throw most of them away on path mismatch.
/// </summary>
public class RefreshHistorySyncTableTask
    : RefreshSyncTaskBase<HistorySyncItem, HistoryWork, (string SourceUserId, string SourceItemId)>
{
    private readonly HistorySyncTableService _historyService;
    private readonly ILibraryManager _libraryManager;

    // Per-run record of source libraries whose discovery broke early —
    // Phase 2 (paginated path discovery) hit the consecutive-error
    // threshold, or Phase 3 (user-data batch fetch) returned null for a
    // batch. Rows belonging to these libraries must be excluded from
    // pruning — otherwise transient source-side hiccups during discovery
    // would silently delete tracking rows for items we just didn't get to
    // enumerate. Reset at the top of <see cref="GetListAsync"/>.
    private readonly HashSet<string> _librariesWithIncompleteDiscovery = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public RefreshHistorySyncTableTask(
        ILogger<RefreshHistorySyncTableTask> logger,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        HistorySyncTableService historyService,
        ILibraryManager libraryManager,
        HistorySyncTableManager manager)
        : base(logger, manager, clientFactory, configManager)
    {
        _historyService = historyService;
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public override string Name => "Refresh History Sync Table";

    /// <inheritdoc />
    public override string Key => "ServerSyncRefreshHistoryTable";

    /// <inheritdoc />
    public override string Description => "Scans source and local servers for watch history differences and updates the history sync table.";

    /// <inheritdoc />
    public override string Category => "History Sync";

    /// <inheritdoc />
    protected override string ModuleMutexKey => "History";

    /// <inheritdoc />
    protected override bool IsEnabled()
    {
        var config = ConfigManager.Configuration;
        if (!config.EnableHistorySync) return false;
        if (string.IsNullOrWhiteSpace(config.SourceServerUrl) || string.IsNullOrWhiteSpace(config.SourceServerApiKey)) return false;
        return config.GetEnabledUserMappings().Count > 0 && config.GetEnabledLibraryMappings().Count > 0;
    }

    // Three-phase fetch:
    //   <list type="number">
    //   <item>Enumerate local items per mapped library, building a path
    //   lookup. In-process, no HTTP.</item>
    //   <item>For each enabled library, do a lightweight source discovery
    //   pass (paths only) and collect the IDs whose translated path exists
    //   locally. One library-level call, not one per user.</item>
    //   <item>For each (enabled user × library), batch-fetch user-data for
    //   the matched IDs only. Per-batch ~50 IDs, all with EnableUserData=true
    //   and the source user's UserId.</item>
    //   </list>
    /// <inheritdoc />
    protected override async Task<IList<HistoryWork>> GetListAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (Client == null)
        {
            return Array.Empty<HistoryWork>();
        }

        _librariesWithIncompleteDiscovery.Clear();

        var config = ConfigManager.Configuration;
        var userMappings = config.UserMappings?.Where(m => m.IsEnabled).ToList() ?? new List<UserMapping>();
        var libraryMappings = config.LibraryMappings?.Where(m => m.IsEnabled).ToList() ?? new List<LibraryMapping>();

        if (userMappings.Count == 0 || libraryMappings.Count == 0)
        {
            return Array.Empty<HistoryWork>();
        }

        progress.Report(2);

        // Phase 1: enumerate local items per library. Path → BaseItem map.
        var localPathsByLibrary = new Dictionary<string, Dictionary<string, BaseItem>>();
        foreach (var mapping in libraryMappings)
        {
            if (string.IsNullOrEmpty(mapping.LocalLibraryId)) continue;
            if (!Guid.TryParse(mapping.LocalLibraryId, out var localLibraryId)) continue;

            var paths = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
            if (_libraryManager.GetItemById(localLibraryId) is Folder root)
            {
                foreach (var item in root.GetRecursiveChildren())
                {
                    if (item is Folder) continue;
                    if (string.IsNullOrEmpty(item.Path)) continue;
                    paths[item.Path] = item;
                }
            }

            localPathsByLibrary[mapping.SourceLibraryId] = paths;
        }

        progress.Report(8);

        // Phase 2: light source discovery — find source IDs of items whose
        // translated path exists locally. One pass per library, regardless
        // of how many users we sync.
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
                var consecutiveErrors = 0;
                const int maxConsecutiveErrors = 3;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    BaseItemDtoQueryResult? page;
                    try
                    {
                        page = await Client.GetLibraryItemPathsAsync(sourceLibraryId, leafTypes, startIndex, pageSize, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Discovery page failed for library {Library} at index {Index}", mapping.SourceLibraryName, startIndex);
                        if (++consecutiveErrors >= maxConsecutiveErrors)
                        {
                            // Parallel.ForEachAsync above — guard the shared
                            // set with a lock. Reads in ShouldSkipPruning
                            // happen after the await completes, so no read
                            // synchronization is needed there.
                            lock (_librariesWithIncompleteDiscovery)
                            {
                                _librariesWithIncompleteDiscovery.Add(mapping.SourceLibraryId);
                            }

                            break;
                        }

                        continue;
                    }

                    consecutiveErrors = 0;
                    if (page?.Items == null || page.Items.Count == 0) break;

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
            "{Task}: discovery matched {Total} items across {Libraries} libraries; will fetch user-data for {Pairs} (user × library) pairs",
            Name,
            totalMatched,
            libraryMappings.Count,
            userMappings.Count * libraryMappings.Count);

        progress.Report(30);

        if (totalMatched == 0)
        {
            return Array.Empty<HistoryWork>();
        }

        // Phase 3: per (user × library), batch-fetch user-data for matched IDs.
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
                for (var i = 0; i < ids.Length; i += batchSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = ids.Skip(i).Take(batchSize).Cast<Guid?>().ToArray();
                    var page = await Client.GetItemsWithUserDataByIdsAsync(sourceUserId, chunk, cancellationToken).ConfigureAwait(false);
                    if (page?.Items == null)
                    {
                        // Batch fetch failed (logged as warning by the client
                        // wrapper). The items in this chunk won't make it into
                        // work, so the base would treat them as stale and prune
                        // their tracking rows. Mark the library incomplete so
                        // ShouldSkipPruning protects them. Serial loop here, no
                        // lock needed.
                        _librariesWithIncompleteDiscovery.Add(libraryMapping.SourceLibraryId);
                        continue;
                    }

                    foreach (var item in page.Items)
                    {
                        work.Add(new HistoryWork(userMapping, libraryMapping, item));
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
        var record = _historyService.BuildRecord(source.UserMapping, source.LibraryMapping, source.SourceItem, sourceItemId);
        return Task.FromResult(record);
    }

    /// <inheritdoc />
    protected override (string SourceUserId, string SourceItemId) ExtractKey(HistorySyncItem record)
        => (record.SourceUserId, record.SourceItemId);

    // In scope when both the row's library mapping AND its user mapping are
    // currently enabled. Disabling either preserves history rows instead of
    // pruning them, so the user's Ignored overrides survive a toggle.
    /// <inheritdoc />
    protected override bool IsInScope(HistorySyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var config = ConfigManager.Configuration;

        var libraryEnabled = false;
        foreach (var mapping in config.GetEnabledLibraryMappings())
        {
            if (string.Equals(mapping.SourceLibraryId, record.SourceLibraryId, StringComparison.OrdinalIgnoreCase))
            {
                libraryEnabled = true;
                break;
            }
        }

        if (!libraryEnabled)
        {
            return false;
        }

        foreach (var mapping in config.GetEnabledUserMappings())
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
    protected override bool ShouldSkipPruning(HistorySyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _librariesWithIncompleteDiscovery.Contains(record.SourceLibraryId);
    }

    /// <inheritdoc />
    protected override void DecideStatus(HistorySyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Status == SyncStatus.Ignored)
        {
            return;
        }

        if (record.HasChanges)
        {
            record.Status = SyncStatus.Queued;
        }
        else
        {
            record.Status = SyncStatus.Synced;
            record.LastSyncTime = DateTime.UtcNow;
        }

        record.StatusDate = DateTime.UtcNow;
        record.Reason = null;
    }

    /// <inheritdoc />
    protected override void RecordRunCompleted(Jellyfin.Plugin.ServerSync.Configuration.PluginConfiguration config, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.LastHistorySyncTime = utcNow;
    }

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(4).Ticks
        }
    };
}
