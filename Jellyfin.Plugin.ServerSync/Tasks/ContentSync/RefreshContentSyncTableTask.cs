using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using TaskTriggerInfo = MediaBrowser.Model.Tasks.TaskTriggerInfo;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Source-side work item for the Content refresh task: a (library mapping,
/// source item) pair plus a flag indicating whether every selected
/// "watched-by-all" user has already played this item — used to short-circuit
/// straight to <see cref="SyncStatus.Ignored"/> in
/// <see cref="UpdateSyncTablesTask.BuildRecordAsync"/>.
/// </summary>
public sealed record ContentRefreshWork(LibraryMapping Mapping, BaseItemDto SourceItem, bool WatchedByAll);

/// <summary>
/// Refresh phase for Content sync. Walks every enabled library mapping,
/// fetches its items, and turns each into a <see cref="SyncItem"/> with the
/// appropriate Pending/Queued/Synced status per the configured approval
/// modes. Items with no local match get a queued/pending row; items already
/// matched and in size-sync get marked Synced; missing items get pruned via
/// <see cref="SyncStateService.ProcessMissingItem"/>.
/// </summary>
public class UpdateSyncTablesTask
    : RefreshSyncTaskBase<SyncItem, ContentRefreshWork, string>
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public UpdateSyncTablesTask(
        ILogger<UpdateSyncTablesTask> logger,
        IPluginConfigurationManager configManager,
        ContentSyncTableManager manager,
        ISourceServerClientFactory clientFactory,
        ILibraryManager libraryManager)
        : base(logger, manager, clientFactory, configManager)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public override string Name => "Refresh Content Sync Table";

    /// <inheritdoc />
    public override string Key => "ServerSyncUpdateTables";

    /// <inheritdoc />
    public override string Description => "Fetches item list from source server and updates the sync tracking table.";

    /// <inheritdoc />
    public override string Category => "Content Sync";

    /// <inheritdoc />
    protected override string ModuleMutexKey => "Content";

    /// <inheritdoc />
    protected override bool IsEnabled()
    {
        var config = ConfigManager.Configuration;
        if (!config.EnableContentSync) return false;
        if (string.IsNullOrWhiteSpace(config.SourceServerUrl) || string.IsNullOrWhiteSpace(config.SourceServerApiKey)) return false;
        return config.GetEnabledLibraryMappings().Count > 0;
    }

    /// <inheritdoc />
    protected override async Task<IList<ContentRefreshWork>> GetListAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (Client == null)
        {
            return Array.Empty<ContentRefreshWork>();
        }

        var config = ConfigManager.Configuration;
        var enabledMappings = config.LibraryMappings?.Where(m => m.IsEnabled).ToList() ?? new List<LibraryMapping>();

        // Pre-fetch per-library counts so per-item progress reporting has a
        // denominator. Each count call is a tiny query with Limit=0; total
        // round-trip is one per enabled library.
        var libraryCounts = new Dictionary<string, int>(enabledMappings.Count);
        var totalExpected = 0;
        foreach (var mapping in enabledMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(mapping.SourceLibraryId, out var sourceLibraryId))
            {
                continue;
            }

            try
            {
                var count = await Client.GetLibraryItemCountAsync(sourceLibraryId, cancellationToken).ConfigureAwait(false);
                libraryCounts[mapping.SourceLibraryId] = count;
                totalExpected += count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogDebug(ex, "Failed to fetch item count for {Library}; progress for this library will be coarse", mapping.SourceLibraryName);
            }
        }

        progress.Report(totalExpected > 0 ? 1 : 50);

        var work = new List<ContentRefreshWork>();
        var fetched = 0;

        foreach (var mapping in enabledMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(mapping.SourceLibraryId, out var sourceLibraryId))
            {
                Logger.LogWarning("Invalid source library ID '{LibraryId}' in mapping for {LibraryName}, skipping",
                    mapping.SourceLibraryId, mapping.SourceLibraryName);
                continue;
            }

            var watchedByAll = await BuildWatchedByAllSetAsync(
                sourceLibraryId,
                config.SkipWatchedByAllUsers,
                config.WatchedFilterUserIds,
                cancellationToken).ConfigureAwait(false);

            await PaginatedFetchUtility.FetchAllPagesAsync(
                fetchPage: (startIndex, batchSize, ct) => Client.GetLibraryItemsAsync(sourceLibraryId, startIndex, batchSize, ct),
                processItem: (item, _) =>
                {
                    var hitWatched = watchedByAll != null && item.Id.HasValue && watchedByAll.Contains(item.Id.Value);
                    work.Add(new ContentRefreshWork(mapping, item, hitWatched));
                    return Task.FromResult(true);
                },
                libraryName: mapping.SourceLibraryName,
                sourceRootPath: mapping.SourceRootPath,
                filterMode: mapping.FilterMode,
                filteredItems: mapping.FilteredItems,
                logger: Logger,
                cancellationToken: cancellationToken,
                onItemProcessed: () =>
                {
                    fetched++;
                    if (totalExpected > 0)
                    {
                        progress.Report(Math.Min(100, 100.0 * fetched / totalExpected));
                    }
                }).ConfigureAwait(false);
        }

        progress.Report(100);
        return work;
    }

    /// <inheritdoc />
    protected override Task<SyncItem?> BuildRecordAsync(
        ContentRefreshWork source,
        IReadOnlyDictionary<string, SyncItem> existing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var sourcePath = source.SourceItem.Path;
        if (string.IsNullOrEmpty(sourcePath))
        {
            return Task.FromResult<SyncItem?>(null);
        }

        var sourceItemId = source.SourceItem.Id!.Value.ToString("N", CultureInfo.InvariantCulture);
        var sourceSize = MediaItemUtilities.GetItemSize(source.SourceItem);
        var sourceCreateDate = source.SourceItem.DateCreated?.DateTime ?? DateTime.UtcNow;
        var localPath = PathUtilities.TranslatePath(sourcePath, source.Mapping.SourceRootPath, source.Mapping.LocalRootPath);

        existing.TryGetValue(sourceItemId, out var existingItem);

        // Watched-by-all takes precedence: mark Ignored regardless of approval mode.
        if (source.WatchedByAll)
        {
            return Task.FromResult<SyncItem?>(BuildWatchedFiltered(source.Mapping, source.SourceItem, sourceItemId, sourceSize, sourceCreateDate, localPath, existingItem));
        }

        var config = ConfigManager.Configuration;

        if (existingItem != null)
        {
            var updated = SyncStateService.ProcessExistingItem(
                existingItem,
                sourcePath,
                sourceSize,
                sourceCreateDate,
                localPath,
                config.ReplaceExistingContentMode,
                config.DetectUpdatedFiles,
                config.SizeMatchToleranceBytes,
                Logger);
            return Task.FromResult<SyncItem?>(updated);
        }

        var fresh = SyncStateService.ProcessNewItem(
            source.Mapping,
            sourceItemId,
            sourcePath,
            sourceSize,
            sourceCreateDate,
            localPath,
            config.DownloadNewContentMode,
            config.SizeMatchToleranceBytes);
        return Task.FromResult<SyncItem?>(fresh);
    }

    /// <inheritdoc />
    protected override string ExtractKey(SyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.SourceItemId;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A row is in scope when its <see cref="SyncItem.SourceLibraryId"/> is
    /// still mapped via an enabled <see cref="LibraryMapping"/>. Rows under a
    /// disabled mapping are inert — neither pruned nor scheduled for deletion.
    /// </remarks>
    protected override bool IsInScope(SyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var enabled = ConfigManager.Configuration.GetEnabledLibraryMappings();
        foreach (var mapping in enabled)
        {
            if (string.Equals(mapping.SourceLibraryId, record.SourceLibraryId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// No-op — <see cref="BuildRecordAsync"/> sets the status itself based
    /// on the configured approval modes (DownloadNewContentMode,
    /// ReplaceExistingContentMode), so the default Queued/Synced decision
    /// would override Content's intent.
    /// </remarks>
    protected override void DecideStatus(SyncItem record)
    {
        // Intentionally empty.
    }

    /// <inheritdoc />
    protected override Task<int> PruneStaleAsync(
        IReadOnlyDictionary<string, SyncItem> existing,
        HashSet<string> seenKeys,
        CancellationToken cancellationToken)
    {
        var deleteMode = ConfigManager.Configuration.DeleteMissingContentMode;
        if (deleteMode == ApprovalMode.Disabled)
        {
            return Task.FromResult(0);
        }

        var typedManager = (ContentSyncTableManager)Manager;
        var pruned = 0;
        foreach (var kvp in existing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (seenKeys.Contains(kvp.Key))
            {
                continue;
            }

            // Skip rows whose library mapping is currently disabled (or removed
            // from config). Without this guard, disabling a mapping silently
            // schedules every synced file under it for deletion.
            if (!IsInScope(kvp.Value))
            {
                continue;
            }

            try
            {
                var result = SyncStateService.ProcessMissingItem(typedManager, kvp.Value, deleteMode, Logger);
                if (result.Changed)
                {
                    pruned++;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to process missing item {SourceItemId}", kvp.Value.SourceItemId);
            }
        }

        return Task.FromResult(pruned);
    }

    /// <inheritdoc />
    protected override Task FinalizeAsync(CancellationToken cancellationToken)
    {
        ResolveLocalItemIds();
        return Task.CompletedTask;
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

    /// <summary>
    /// Builds the set of source items that every selected user has played
    /// in the given library. Returns null when the filter is disabled or
    /// no users are selected.
    /// </summary>
    private async Task<HashSet<Guid>?> BuildWatchedByAllSetAsync(
        Guid libraryId,
        bool skipWatchedByAllUsers,
        List<string> watchedFilterUserIds,
        CancellationToken cancellationToken)
    {
        if (!skipWatchedByAllUsers || watchedFilterUserIds == null || watchedFilterUserIds.Count == 0 || Client == null)
        {
            return null;
        }

        HashSet<Guid>? intersection = null;

        foreach (var userIdStr in watchedFilterUserIds)
        {
            if (!Guid.TryParse(userIdStr, out var userId))
            {
                // An unparseable filter user means the configuration is broken
                // for that slot. Disable the filter entirely rather than silently
                // dropping the user (which would treat them as a wildcard and
                // cause items to be wrongly classified as "watched by all").
                Logger.LogWarning(
                    "Watched-by-all filter disabled: invalid user ID {UserId} in WatchedFilterUserIds. Fix the configuration to re-enable the filter.",
                    userIdStr);
                return null;
            }

            var played = await Client.GetUserPlayedItemIdsAsync(userId, libraryId, cancellationToken).ConfigureAwait(false);

            if (intersection == null)
            {
                intersection = played;
            }
            else
            {
                intersection.IntersectWith(played);
            }

            if (intersection.Count == 0)
            {
                break;
            }
        }

        return intersection ?? new HashSet<Guid>();
    }

    /// <summary>
    /// Builds an Ignored record for an item that every selected user has
    /// played. If a record already exists in Synced/Ignored we leave it
    /// alone; otherwise we update it (or create a new one) with Ignored.
    /// </summary>
    private static SyncItem? BuildWatchedFiltered(
        LibraryMapping mapping,
        BaseItemDto sourceItem,
        string sourceItemId,
        long sourceSize,
        DateTime sourceCreateDate,
        string localPath,
        SyncItem? existing)
    {
        if (existing != null && (existing.Status == SyncStatus.Ignored || existing.Status == SyncStatus.Synced))
        {
            return existing;
        }

        var item = existing ?? new SyncItem
        {
            SourceLibraryId = mapping.SourceLibraryId,
            LocalLibraryId = mapping.LocalLibraryId,
            SourceItemId = sourceItemId
        };

        item.SourcePath = sourceItem.Path!;
        item.SourceSize = sourceSize;
        item.SourceCreateDate = sourceCreateDate;
        item.LocalPath = localPath;
        item.Status = SyncStatus.Ignored;
        item.PendingType = null;
        item.StatusDate = DateTime.UtcNow;
        return item;
    }

    /// <summary>
    /// Resolves <see cref="SyncItem.LocalItemId"/> for synced items that
    /// don't have one yet, by looking the path up in the local library.
    /// </summary>
    private void ResolveLocalItemIds()
    {
        var typedManager = (ContentSyncTableManager)Manager;
        var syncedItems = typedManager.GetByStatus(SyncStatus.Synced);
        var resolved = 0;

        foreach (var item in syncedItems)
        {
            if (!string.IsNullOrEmpty(item.LocalItemId) || string.IsNullOrEmpty(item.LocalPath))
            {
                continue;
            }

            try
            {
                var localItem = _libraryManager.FindByPath(item.LocalPath, isFolder: false);
                if (localItem != null)
                {
                    item.LocalItemId = localItem.Id.ToString();
                    typedManager.Upsert(item);
                    resolved++;
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to resolve LocalItemId for {FileName}", System.IO.Path.GetFileName(item.LocalPath));
            }
        }

        if (resolved > 0)
        {
            Logger.LogInformation("Resolved {Count} local item IDs", resolved);
        }
    }
}
