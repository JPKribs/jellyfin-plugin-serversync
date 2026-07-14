using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// Content sync endpoints for Server Sync plugin.
/// </summary>
public partial class ConfigurationController
{
    /// <summary>
    /// Gets paginated sync items from the database with optional search and filter.
    /// </summary>
    /// <param name="search">Optional search term.</param>
    /// <param name="status">Optional status filter.</param>
    /// <param name="pendingType">Optional pending type filter.</param>
    /// <param name="skip">Number of items to skip (default 0).</param>
    /// <param name="take">Maximum items to return (default 50, max 200).</param>
    /// <returns>Paginated result of sync item DTOs.</returns>
    [HttpGet("Items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PagedResult<SyncItemDto>> GetSyncItems(
        [FromServices] ContentSyncTableManager manager,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? pendingType = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50)
    {
        ArgumentNullException.ThrowIfNull(manager);
        var config = _configManager.Configuration;

        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);

        SyncStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<SyncStatus>(status, out var parsedStatus) && Enum.IsDefined(parsedStatus))
        {
            statusFilter = parsedStatus;
        }

        PendingType? pendingTypeFilter = null;
        if (!string.IsNullOrEmpty(pendingType) && Enum.TryParse<PendingType>(pendingType, out var parsedPendingType) && Enum.IsDefined(parsedPendingType))
        {
            pendingTypeFilter = parsedPendingType;
        }

        var (items, totalCount) = manager.SearchPaginated(search, statusFilter, pendingTypeFilter, skip, take);

        var libraryMappings = config.LibraryMappings ?? new List<LibraryMapping>();
        var libraryNameLookup = libraryMappings
            .GroupBy(m => m.SourceLibraryId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (SourceName: g.First().SourceLibraryName, LocalName: g.First().LocalLibraryName),
                StringComparer.OrdinalIgnoreCase);

        return Ok(new PagedResult<SyncItemDto>(
            items.Select(i =>
            {
                libraryNameLookup.TryGetValue(i.SourceLibraryId, out var libraryNames);
                return new SyncItemDto
                {
                    Id = i.Id,
                    SourceItemId = i.SourceItemId,
                    SourceLibraryId = i.SourceLibraryId,
                    SourceLibraryName = libraryNames.SourceName,
                    LocalLibraryId = i.LocalLibraryId,
                    LocalLibraryName = libraryNames.LocalName,
                    SourcePath = i.SourcePath,
                    LocalPath = i.LocalPath,
                    SourceSize = i.SourceSize,
                    SourceSizeFormatted = FormatUtilities.FormatBytes(i.SourceSize),
                    SourceCreateDate = i.SourceCreateDate,
                    LocalItemId = i.LocalItemId,
                    Status = i.Status.ToString(),
                    PendingType = i.PendingType?.ToString(),
                    StatusDate = i.StatusDate,
                    LastSyncTime = i.LastSyncTime,
                    ErrorMessage = i.Reason,
                    RetryCount = i.RetryCount,
                    SourceServerUrl = !string.IsNullOrEmpty(config.SourceServerExternalUrl) ? config.SourceServerExternalUrl : config.SourceServerUrl,
                    SourceServerId = config.SourceServerId,
                    CompanionFiles = i.CompanionFiles
                };
            }).ToList(),
            totalCount,
            skip,
            take));
    }

    /// <summary>
    /// Gets sync status counts.
    /// </summary>
    /// <returns>Sync status response with counts.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SyncStatusResponse> GetSyncStatus([FromServices] ContentSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        var counts = manager.GetStatusCounts();
        var pendingCounts = manager.GetPendingCounts();

        var response = new SyncStatusResponse
        {
            Pending = counts.GetValueOrDefault(SyncStatus.Pending, 0),
            PendingDownload = pendingCounts.GetValueOrDefault(PendingType.Download, 0),
            PendingReplacement = pendingCounts.GetValueOrDefault(PendingType.Replacement, 0),
            PendingDeletion = pendingCounts.GetValueOrDefault(PendingType.Deletion, 0),
            Queued = counts.GetValueOrDefault(SyncStatus.Queued, 0),
            Synced = counts.GetValueOrDefault(SyncStatus.Synced, 0),
            Errored = counts.GetValueOrDefault(SyncStatus.Errored, 0),
            Ignored = counts.GetValueOrDefault(SyncStatus.Ignored, 0),
            Deleting = counts.GetValueOrDefault(SyncStatus.Deleting, 0)
        };
        PopulateLastFailure(response, "Content");
        return Ok(response);
    }

    /// <summary>
    /// Gets detailed sync statistics for health dashboard.
    /// </summary>
    /// <returns>Sync stats response.</returns>
    [HttpGet("Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<SyncStatsResponse> GetSyncStats([FromServices] ContentSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        try
        {
            var config = _configManager.Configuration;
            var stats = manager.GetSyncStats();
            var pendingCounts = manager.GetPendingCounts();
            var diskInfo = DiskSpaceService.GetMinimumDiskSpaceInfo(config);

            return Ok(new SyncStatsResponse
            {
                TotalItems = stats.StatusCounts.Values.Sum(),
                SyncedItems = stats.StatusCounts.GetValueOrDefault(SyncStatus.Synced, 0),
                QueuedItems = stats.StatusCounts.GetValueOrDefault(SyncStatus.Queued, 0),
                ErroredItems = stats.StatusCounts.GetValueOrDefault(SyncStatus.Errored, 0),
                PendingItems = stats.StatusCounts.GetValueOrDefault(SyncStatus.Pending, 0),
                PendingDownloadItems = pendingCounts.GetValueOrDefault(PendingType.Download, 0),
                PendingReplacementItems = pendingCounts.GetValueOrDefault(PendingType.Replacement, 0),
                PendingDeletionItems = pendingCounts.GetValueOrDefault(PendingType.Deletion, 0),
                IgnoredItems = stats.StatusCounts.GetValueOrDefault(SyncStatus.Ignored, 0),
                TotalSyncedBytes = stats.TotalSyncedBytes,
                TotalQueuedBytes = stats.TotalQueuedBytes,
                LastSyncTime = stats.LastSyncTime,
                LastSyncStartTime = config.LastSyncStartTime,
                LastSyncEndTime = config.LastSyncEndTime,
                FreeDiskSpaceBytes = diskInfo?.FreeBytes ?? 0,
                MinimumRequiredBytes = (long)config.MinimumFreeDiskSpaceGb * 1024 * 1024 * 1024,
                HasSufficientDiskSpace = diskInfo?.IsSufficient ?? true
            });
        }
        catch (ObjectDisposedException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Database is not available");
        }
    }

    /// <summary>
    /// Gets the total size of pending items to be synced.
    /// Calculates: PendingDownload + PendingReplacement + Queued - PendingDeletion
    /// </summary>
    /// <returns>Pending size response with breakdown.</returns>
    [HttpGet("PendingSize")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PendingSizeResponse> GetPendingSize([FromServices] ContentSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        var sizes = manager.GetPendingSizes();

        return Ok(new PendingSizeResponse
        {
            PendingDownloadBytes = sizes.GetValueOrDefault("PendingDownload", 0),
            PendingReplacementBytes = sizes.GetValueOrDefault("PendingReplacement", 0),
            PendingDeletionBytes = sizes.GetValueOrDefault("PendingDeletion", 0),
            QueuedBytes = sizes.GetValueOrDefault("Queued", 0),
            TotalPendingBytes = sizes.GetValueOrDefault("Total", 0)
        });
    }

    /// <summary>
    /// Gets disk space information for configured library paths.
    /// </summary>
    /// <returns>List of disk space info.</returns>
    [HttpGet("DiskSpace")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<DiskSpaceInfo>> GetDiskSpace()
    {
        return Ok(DiskSpaceService.GetDiskSpaceInfo(_configManager.Configuration));
    }

    /// <summary>
    /// Manually triggers the sync task.
    /// </summary>
    /// <returns>Action result with status message.</returns>
    [HttpPost("TriggerSync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerSync()
        => ExecuteScheduledTaskByKey("ServerSyncDownloadContent", "Sync task started", "Download task not found");

    /// <summary>
    /// Manually triggers the refresh sync table task.
    /// </summary>
    /// <returns>Action result with status message.</returns>
    [HttpPost("TriggerRefresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerRefresh()
        => ExecuteScheduledTaskByKey("ServerSyncUpdateTables", "Refresh task started", "Refresh task not found");

    /// <summary>
    /// Resets errored items for retry.
    /// </summary>
    /// <param name="request">Optional request with specific item IDs.</param>
    /// <returns>Action result with success status.</returns>
    [HttpPost("RetryErroredItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult RetryErroredItems(
        [FromServices] ContentSyncTableManager manager,
        [FromBody] BulkItemsRequest? request = null)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (request?.SourceItemIds?.Count > 0)
        {
            foreach (var itemId in request.SourceItemIds)
            {
                manager.UpdateStatus(itemId, SyncStatus.Queued);
            }
        }
        else
        {
            var erroredItems = manager.GetByStatus(SyncStatus.Errored);
            foreach (var item in erroredItems)
            {
                manager.UpdateStatus(item.SourceItemId, SyncStatus.Queued);
            }
        }

        return Ok(new { Success = true });
    }

    /// <summary>
    /// Updates the status of a sync item.
    /// </summary>
    /// <param name="request">Status update request.</param>
    /// <returns>Action result with success status.</returns>
    [HttpPost("UpdateItemStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult UpdateItemStatus(
        [FromServices] ContentSyncTableManager manager,
        [FromBody] UpdateItemStatusRequest request)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.TryParse<SyncStatus>(request.Status, out var status) || !Enum.IsDefined(status)
            || status == SyncStatus.Deleting)
        {
            // Deleting schedules unattended file removal; it is only ever set
            // by the refresh pipeline or the approval flow, never directly.

            return BadRequest("Invalid status value");
        }

        manager.UpdateStatus(request.SourceItemId, status);
        return Ok(new { Success = true });
    }

    /// <summary>
    /// Marks multiple items as ignored.
    /// </summary>
    /// <param name="request">Bulk items request.</param>
    /// <returns>Action result with updated count.</returns>
    [HttpPost("IgnoreItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult IgnoreItems(
        [FromServices] ContentSyncTableManager manager,
        [FromBody] BulkItemsRequest request)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (request?.SourceItemIds == null || request.SourceItemIds.Count == 0)
        {
            return BadRequest("No items specified");
        }

        var successCount = 0;
        foreach (var itemId in request.SourceItemIds)
        {
            try
            {
                manager.UpdateStatus(itemId, SyncStatus.Ignored);
                successCount++;
            }
            catch (Exception ex)
            {
                var sanitizedId = SanitizeForLog(itemId);
                _logger.LogWarning(ex, "Failed to update status for item {ItemId}", sanitizedId);
            }
        }

        return Ok(new { Updated = successCount });
    }

    /// <summary>
    /// Moves items to Queued status (works for Pending, Ignored, Errored, and Synced items).
    /// </summary>
    /// <param name="request">Bulk items request.</param>
    /// <returns>Action result with updated count.</returns>
    [HttpPost("QueueItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult QueueItems(
        [FromServices] ContentSyncTableManager manager,
        [FromBody] BulkItemsRequest request)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (request?.SourceItemIds == null || request.SourceItemIds.Count == 0)
        {
            return BadRequest("No items specified");
        }

        var successCount = 0;
        foreach (var itemId in request.SourceItemIds)
        {
            try
            {
                manager.UpdateStatus(itemId, SyncStatus.Queued);
                successCount++;
            }
            catch (Exception ex)
            {
                var sanitizedId = SanitizeForLog(itemId);
                _logger.LogWarning(ex, "Failed to update status for item {ItemId}", sanitizedId);
            }
        }

        return Ok(new { Updated = successCount });
    }

    /// <summary>
    /// Marks items as synced after verifying the local file exists.
    /// Use this to resolve items that are stuck in Queued/Errored status
    /// when the local file is already present.
    /// </summary>
    /// <param name="request">Bulk items request.</param>
    /// <returns>Action result with synced and not-found counts.</returns>
    [HttpPost("MarkSynced")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult MarkSynced(
        [FromServices] ContentSyncTableManager manager,
        [FromBody] BulkItemsRequest request)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (request?.SourceItemIds == null || request.SourceItemIds.Count == 0)
        {
            return BadRequest("No items specified");
        }

        var syncedCount = 0;
        var notFoundCount = 0;

        foreach (var itemId in request.SourceItemIds)
        {
            try
            {
                var item = manager.GetBySourceItemId(itemId);
                if (item == null)
                {
                    notFoundCount++;
                    continue;
                }

                if (!string.IsNullOrEmpty(item.LocalPath) && System.IO.File.Exists(item.LocalPath))
                {
                    manager.UpdateStatus(itemId, SyncStatus.Synced);
                    syncedCount++;
                    _logger.LogInformation(
                        "Manually marked {FileName} as synced (local file verified at {LocalPath})",
                        SanitizeForLog(Path.GetFileName(item.LocalPath)),
                        SanitizeForLog(item.LocalPath));
                }
                else
                {
                    notFoundCount++;
                    _logger.LogWarning(
                        "Cannot mark {ItemId} as synced: local file not found at {LocalPath}",
                        SanitizeForLog(itemId),
                        SanitizeForLog(item.LocalPath));
                }
            }
            catch (Exception ex)
            {
                var sanitizedId = SanitizeForLog(itemId);
                _logger.LogWarning(ex, "Failed to mark item {ItemId} as synced", sanitizedId);
                notFoundCount++;
            }
        }

        return Ok(new { Synced = syncedCount, NotFound = notFoundCount });
    }

    /// <summary>
    /// Deletes items from the LOCAL server only using the Jellyfin API.
    /// </summary>
    /// <param name="request">Bulk items request.</param>
    /// <returns>Action result with deletion counts.</returns>
    [HttpPost("DeleteLocalItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult DeleteLocalItems(
        [FromServices] ContentSyncTableManager manager,
        [FromBody] BulkItemsRequest request)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (request?.SourceItemIds == null || request.SourceItemIds.Count == 0)
        {
            return BadRequest("No items specified");
        }

        var config = _configManager.Configuration;
        var deletedCount = 0;
        var failedCount = 0;
        var skippedCount = 0;

        _logger.LogInformation("Starting deletion of {Count} items from local server", request.SourceItemIds.Count);

        foreach (var sourceItemId in request.SourceItemIds)
        {
            var item = manager.GetBySourceItemId(sourceItemId);
            if (item == null)
            {
                var sanitizedSourceId = SanitizeForLog(sourceItemId);
                _logger.LogWarning("SKIPPED DELETE: Item {SourceItemId} not found in tracking database", sanitizedSourceId);
                skippedCount++;
                continue;
            }

            var fileName = string.IsNullOrEmpty(item.LocalPath) ? null : Path.GetFileName(item.LocalPath);
            var sanitizedFileName = SanitizeForLog(fileName);
            var sanitizedLocalPath = SanitizeForLog(item.LocalPath);

            if (!string.IsNullOrEmpty(item.LocalPath) && !FileValidationService.IsPathWithinLibrary(item.LocalPath, config))
            {
                _logger.LogWarning(
                    "SKIPPED DELETE: {FileName} - Path is outside configured library directories. Local path: {LocalPath}",
                    sanitizedFileName,
                    sanitizedLocalPath);
                skippedCount++;
                continue;
            }

            if (!string.IsNullOrEmpty(item.LocalPath))
            {
                var containingRoot = FileValidationService.GetContainingLibraryRoot(item.LocalPath, config);
                if (containingRoot != null && FileValidationService.HasSymlinkedDirectoryComponent(item.LocalPath, containingRoot))
                {
                    _logger.LogWarning(
                        "SKIPPED DELETE: {FileName} - Path traverses a symlinked directory; the physical file may live outside the library. Local path: {LocalPath}",
                        sanitizedFileName,
                        sanitizedLocalPath);
                    skippedCount++;
                    continue;
                }
            }

            MediaBrowser.Controller.Entities.BaseItem? localItem = null;

            if (!string.IsNullOrEmpty(item.LocalItemId) && Guid.TryParse(item.LocalItemId, out var localItemGuid))
            {
                localItem = _libraryManager.GetItemById(localItemGuid);
            }

            if (localItem == null && !string.IsNullOrEmpty(item.LocalPath))
            {
                localItem = _libraryManager.FindByPath(item.LocalPath, isFolder: false);
            }

            // The id-based delete removes whatever file the Jellyfin item
            // points at, and LocalItemId can go stale when a mapping's root
            // changes — so the RESOLVED path must pass the same checks the
            // tracked path did, and must actually be the tracked file.
            if (localItem != null)
            {
                var resolvedPath = localItem.Path;
                var mismatch = !string.IsNullOrEmpty(item.LocalPath)
                    && !string.Equals(resolvedPath, item.LocalPath, StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrEmpty(resolvedPath)
                    || mismatch
                    || !FileValidationService.IsPathWithinLibrary(resolvedPath, config))
                {
                    _logger.LogWarning(
                        "Stale LocalItemId for {FileName}: resolved item path {ResolvedPath} does not match the tracked path or escapes library roots; falling back to the tracked path",
                        sanitizedFileName,
                        SanitizeForLog(resolvedPath));
                    localItem = null;
                }
            }

            var fileExists = localItem != null || (!string.IsNullOrEmpty(item.LocalPath) && System.IO.File.Exists(item.LocalPath));

            if (!fileExists)
            {
                // File doesn't exist anywhere - remove from tracking since there's nothing to delete
                var sanitizedSourcePath = SanitizeForLog(item.SourcePath);
                _logger.LogWarning(
                    "SKIPPED DELETE: {FileName} - File not found in Jellyfin library or on disk, removing from tracking. Source path: {SourcePath}",
                    sanitizedFileName,
                    sanitizedSourcePath);
                manager.Delete(sourceItemId);
                skippedCount++;
                continue;
            }

            var result = FileDeletionService.DeleteLocalFile(item.LocalPath, localItem, _libraryManager, config, _logger);

            if (result.Success)
            {
                var useRecyclingBin = config.EnableRecyclingBin && !string.IsNullOrEmpty(config.RecyclingBinPath);
                var action = useRecyclingBin ? "RECYCLED" : "DELETED";
                var suffix = localItem == null ? " (direct)" : string.Empty;
                _logger.LogInformation("{Action}{Suffix}: {FileName} - Local path: {LocalPath}", action, suffix, sanitizedFileName, sanitizedLocalPath);
                manager.Delete(sourceItemId);
                deletedCount++;
            }
            else
            {
                // Check if file still exists after failed deletion attempt
                // It may have been deleted externally (manually or by another process)
                var stillExists = !string.IsNullOrEmpty(item.LocalPath) && System.IO.File.Exists(item.LocalPath);
                if (!stillExists)
                {
                    // File is gone (deleted elsewhere) - remove from tracking
                    _logger.LogInformation(
                        "DELETE (external): {FileName} - File no longer exists after deletion attempt, removing from tracking. Local path: {LocalPath}",
                        sanitizedFileName,
                        sanitizedLocalPath);
                    manager.Delete(sourceItemId);
                    deletedCount++;
                }
                else
                {
                    var sanitizedError = SanitizeForLog(result.ErrorMessage);
                    _logger.LogError("FAILED DELETE: {FileName} - {Error}. Local path: {LocalPath}", sanitizedFileName, sanitizedError, sanitizedLocalPath);
                    failedCount++;
                }
            }
        }

        _logger.LogInformation(
            "Deletion task complete: {Deleted} deleted, {Failed} failed, {Skipped} skipped out of {Total} items",
            deletedCount,
            failedCount,
            skippedCount,
            request.SourceItemIds.Count);

        return Ok(new { Deleted = deletedCount, Failed = failedCount, Skipped = skippedCount });
    }

    /// <summary>
    /// Removes items from the sync tracking database without deleting the actual files.
    /// </summary>
    /// <param name="request">Bulk items request.</param>
    /// <returns>Action result with removed count.</returns>
    [HttpPost("RemoveFromTracking")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult RemoveFromTracking(
        [FromServices] ContentSyncTableManager manager,
        [FromBody] BulkItemsRequest request)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (request?.SourceItemIds == null || request.SourceItemIds.Count == 0)
        {
            return BadRequest("No items specified");
        }

        var successCount = 0;
        foreach (var sourceItemId in request.SourceItemIds)
        {
            try
            {
                manager.Delete(sourceItemId);
                successCount++;
            }
            catch (Exception ex)
            {
                var sanitizedId = SanitizeForLog(sourceItemId);
                _logger.LogWarning(ex, "Failed to remove tracking for item {ItemId}", sanitizedId);
            }
        }

        return Ok(new { Removed = successCount });
    }
}
