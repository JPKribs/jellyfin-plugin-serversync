using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Service for synchronizing the sync table with the source server.
/// </summary>
public class SyncTableService
{
    private readonly ILogger<SyncTableService> _logger;
    private readonly ILibraryManager _libraryManager;

    public SyncTableService(ILogger<SyncTableService> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Processes a single library mapping, fetching items and updating the sync database.
    /// </summary>
    /// <param name="client">Source server client.</param>
    /// <param name="database">Sync database.</param>
    /// <param name="mapping">Library mapping to process.</param>
    /// <param name="downloadNewMode">Mode for handling new items.</param>
    /// <param name="replaceExistingMode">Mode for handling existing items.</param>
    /// <param name="deleteMissingMode">Mode for handling missing items.</param>
    /// <param name="detectUpdatedFiles">Whether to detect updated files.</param>
    /// <param name="changeDetectionPolicy">Policy for detecting source changes.</param>
    /// <param name="sizeMatchToleranceBytes">Tolerance in bytes for size comparison (0 = strict).</param>
    /// <param name="skipWatchedByAllUsers">When true, skip items watched by every user in <paramref name="watchedFilterUserIds"/>.</param>
    /// <param name="watchedFilterUserIds">Source-server user IDs whose watched status determines the watched filter. Empty disables the filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="onItemProcessed">Optional callback invoked after each item is processed.</param>
    /// <returns>Number of items processed.</returns>
    public async Task<int> ProcessLibraryAsync(
        SourceServerClient client,
        SyncDatabase database,
        LibraryMapping mapping,
        ApprovalMode downloadNewMode,
        ApprovalMode replaceExistingMode,
        ApprovalMode deleteMissingMode,
        bool detectUpdatedFiles,
        ChangeDetectionPolicy changeDetectionPolicy,
        long sizeMatchToleranceBytes,
        bool skipWatchedByAllUsers,
        IReadOnlyCollection<string> watchedFilterUserIds,
        CancellationToken cancellationToken,
        Action? onItemProcessed = null)
    {
        var processedItems = 0;

        Dictionary<string, SyncItem> existingItems;
        try
        {
            var items = database.GetBySourceLibrary(mapping.SourceLibraryId);
            existingItems = new Dictionary<string, SyncItem>();
            foreach (var item in items)
            {
                existingItems[item.SourceItemId] = item;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load existing items for library {LibraryName}", mapping.SourceLibraryName);
            return 0;
        }

        var seenSourceItemIds = new HashSet<string>();

        // Mark existing items matching the filter as Ignored and exclude them from missing-item processing
        if (mapping.FilterMode != LibraryFilterMode.AllowAll && mapping.FilteredItems?.Count > 0)
        {
            foreach (var kvp in existingItems)
            {
                if (PathUtilities.IsItemFiltered(kvp.Value.SourcePath, mapping.SourceRootPath, mapping.FilterMode, mapping.FilteredItems))
                {
                    // Prevent ProcessMissingItems from treating filtered items as deleted
                    seenSourceItemIds.Add(kvp.Key);

                    if (kvp.Value.Status != SyncStatus.Ignored)
                    {
                        kvp.Value.Status = SyncStatus.Ignored;
                        kvp.Value.PendingType = null;
                        kvp.Value.StatusDate = DateTime.UtcNow;
                        database.Upsert(kvp.Value);
                        _logger.LogInformation("Marked {FileName} as ignored (filtered by library filter)", System.IO.Path.GetFileName(kvp.Value.SourcePath));
                    }
                }
            }
        }

        var sourceLibraryGuid = Guid.Parse(mapping.SourceLibraryId);

        var watchedByAllSet = await BuildWatchedByAllSetAsync(
            client, sourceLibraryGuid, skipWatchedByAllUsers, watchedFilterUserIds, cancellationToken).ConfigureAwait(false);

        processedItems = await PaginatedFetchUtility.FetchAllPagesAsync(
            fetchPage: (startIndex, batchSize, ct) => client.GetLibraryItemsAsync(sourceLibraryGuid, startIndex, batchSize, ct),
            processItem: (item, _) =>
            {
                var sourceItemId = item.Id!.Value.ToString("N", CultureInfo.InvariantCulture);
                seenSourceItemIds.Add(sourceItemId);

                if (watchedByAllSet != null && watchedByAllSet.Contains(item.Id.Value))
                {
                    MarkWatchedFiltered(database, mapping, item, existingItems);
                    return Task.FromResult(true);
                }

                ProcessItem(database, mapping, item, existingItems, downloadNewMode, replaceExistingMode, detectUpdatedFiles, changeDetectionPolicy, sizeMatchToleranceBytes);
                return Task.FromResult(true);
            },
            libraryName: mapping.SourceLibraryName,
            sourceRootPath: mapping.SourceRootPath,
            filterMode: mapping.FilterMode,
            filteredItems: mapping.FilteredItems,
            logger: _logger,
            cancellationToken: cancellationToken,
            onItemProcessed: onItemProcessed).ConfigureAwait(false);

        // Handle items that exist in our database but no longer exist on the source
        if (deleteMissingMode != ApprovalMode.Disabled)
        {
            try
            {
                ProcessMissingItems(database, existingItems, seenSourceItemIds, deleteMissingMode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process missing items for library {LibraryName}", mapping.SourceLibraryName);
            }
        }

        _logger.LogInformation("Processed {Count} items from {LibraryName}", processedItems, mapping.SourceLibraryName);

        return processedItems;
    }

    /// <summary>
    /// Resolves LocalItemId for synced items by looking them up in the local Jellyfin library.
    /// </summary>
    /// <param name="database">Sync database.</param>
    /// <returns>Number of items resolved.</returns>
    public int ResolveLocalItemIds(SyncDatabase database)
    {
        var syncedItems = database.GetByStatus(SyncStatus.Synced);
        var resolvedCount = 0;

        foreach (var item in syncedItems)
        {
            if (!string.IsNullOrEmpty(item.LocalItemId))
            {
                continue;
            }

            if (string.IsNullOrEmpty(item.LocalPath))
            {
                continue;
            }

            try
            {
                var localItem = _libraryManager.FindByPath(item.LocalPath, isFolder: false);
                if (localItem != null)
                {
                    item.LocalItemId = localItem.Id.ToString();
                    database.Upsert(item);
                    resolvedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to resolve LocalItemId for {FileName}", System.IO.Path.GetFileName(item.LocalPath));
            }
        }

        if (resolvedCount > 0)
        {
            _logger.LogInformation("Resolved {Count} local item IDs", resolvedCount);
        }

        return resolvedCount;
    }

    /// <summary>
    /// Processes items that exist locally but no longer exist on the source server.
    /// </summary>
    private void ProcessMissingItems(
        SyncDatabase database,
        Dictionary<string, SyncItem> existingItems,
        HashSet<string> seenSourceItemIds,
        ApprovalMode deleteMissingMode)
    {
        foreach (var kvp in existingItems)
        {
            if (seenSourceItemIds.Contains(kvp.Key))
            {
                continue;
            }

            try
            {
                SyncStateService.ProcessMissingItem(database, kvp.Value, deleteMissingMode, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process missing item {SourceItemId}", kvp.Value.SourceItemId);
            }
        }
    }

    /// <summary>
    /// Processes a single item from the source server and updates the database accordingly.
    /// </summary>
    private void ProcessItem(
        SyncDatabase database,
        LibraryMapping mapping,
        BaseItemDto item,
        Dictionary<string, SyncItem> existingItems,
        ApprovalMode downloadNewMode,
        ApprovalMode replaceExistingMode,
        bool detectUpdatedFiles,
        ChangeDetectionPolicy changeDetectionPolicy,
        long sizeMatchToleranceBytes)
    {
        var sourceItemId = item.Id!.Value.ToString("N", CultureInfo.InvariantCulture);
        var sourceSize = MediaItemUtilities.GetItemSize(item);
        var sourceCreateDate = item.DateCreated?.DateTime ?? DateTime.UtcNow;
        var sourceETag = item.Etag;
        var localPath = PathUtilities.TranslatePath(item.Path!, mapping.SourceRootPath, mapping.LocalRootPath);

        var existingItem = existingItems.GetValueOrDefault(sourceItemId);

        if (existingItem != null)
        {
            SyncStateService.ProcessExistingItem(
                database,
                existingItem,
                item.Path!,
                sourceSize,
                sourceCreateDate,
                sourceETag,
                localPath,
                replaceExistingMode,
                detectUpdatedFiles,
                changeDetectionPolicy,
                sizeMatchToleranceBytes,
                _logger);
        }
        else
        {
            SyncStateService.ProcessNewItem(
                database,
                mapping,
                sourceItemId,
                item.Path!,
                sourceSize,
                sourceCreateDate,
                sourceETag,
                localPath,
                downloadNewMode,
                sizeMatchToleranceBytes);
        }
    }

    /// <summary>
    /// Builds the set of item IDs that every selected user has played in the given library.
    /// Returns null when the filter is disabled or no users are selected. An empty set means
    /// the filter is active but at least one user has no played items in the library.
    /// </summary>
    private async Task<HashSet<Guid>?> BuildWatchedByAllSetAsync(
        SourceServerClient client,
        Guid libraryId,
        bool skipWatchedByAllUsers,
        IReadOnlyCollection<string> watchedFilterUserIds,
        CancellationToken cancellationToken)
    {
        if (!skipWatchedByAllUsers || watchedFilterUserIds == null || watchedFilterUserIds.Count == 0)
        {
            return null;
        }

        HashSet<Guid>? intersection = null;

        foreach (var userIdStr in watchedFilterUserIds)
        {
            if (!Guid.TryParse(userIdStr, out var userId))
            {
                _logger.LogWarning("Skipping watched-filter user with invalid ID: {UserId}", userIdStr);
                continue;
            }

            var played = await client.GetUserPlayedItemIdsAsync(userId, libraryId, cancellationToken).ConfigureAwait(false);

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
    /// Marks an item as Ignored because every selected user has played it.
    /// Persists a new tracking row for items not yet in the database so the user can see
    /// what was skipped, and updates pre-existing rows that aren't already Ignored or Synced.
    /// </summary>
    private void MarkWatchedFiltered(
        SyncDatabase database,
        LibraryMapping mapping,
        BaseItemDto item,
        Dictionary<string, SyncItem> existingItems)
    {
        var sourceItemId = item.Id!.Value.ToString("N", CultureInfo.InvariantCulture);
        var sourceSize = MediaItemUtilities.GetItemSize(item);
        var sourceCreateDate = item.DateCreated?.DateTime ?? DateTime.UtcNow;
        var sourceETag = item.Etag;
        var localPath = PathUtilities.TranslatePath(item.Path!, mapping.SourceRootPath, mapping.LocalRootPath);

        var existing = existingItems.GetValueOrDefault(sourceItemId);
        if (existing != null)
        {
            // Leave Synced and already-Ignored items alone.
            if (existing.Status == SyncStatus.Ignored || existing.Status == SyncStatus.Synced)
            {
                return;
            }

            existing.Status = SyncStatus.Ignored;
            existing.PendingType = null;
            existing.StatusDate = DateTime.UtcNow;
            existing.SourcePath = item.Path!;
            existing.SourceSize = sourceSize;
            existing.SourceCreateDate = sourceCreateDate;
            existing.SourceETag = sourceETag;
            existing.LocalPath = localPath;
            database.Upsert(existing);
            _logger.LogInformation("Marked {FileName} as ignored (watched by all selected users)", System.IO.Path.GetFileName(item.Path));
            return;
        }

        var syncItem = new SyncItem
        {
            SourceLibraryId = mapping.SourceLibraryId,
            LocalLibraryId = mapping.LocalLibraryId,
            SourceItemId = sourceItemId,
            SourcePath = item.Path!,
            SourceSize = sourceSize,
            SourceCreateDate = sourceCreateDate,
            SourceETag = sourceETag,
            LocalPath = localPath,
            StatusDate = DateTime.UtcNow,
            Status = SyncStatus.Ignored
        };

        database.Upsert(syncItem);
        existingItems[sourceItemId] = syncItem;
        _logger.LogInformation("Tracked {FileName} as ignored (watched by all selected users)", System.IO.Path.GetFileName(item.Path));
    }
}
