using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Services;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// History sync endpoints for Server Sync plugin.
/// </summary>
public partial class ConfigurationController
{
    // ===== History Sync Endpoints =====

    /// <summary>
    /// Gets paginated history sync items from the database with optional search and filter.
    /// </summary>
    /// <param name="search">Optional search term.</param>
    /// <param name="status">Optional status filter.</param>
    /// <param name="sourceUserId">Optional source user ID filter.</param>
    /// <param name="skip">Number of items to skip (default 0).</param>
    /// <param name="take">Maximum items to return (default 50, max 200).</param>
    /// <returns>Paginated result of history sync item DTOs.</returns>
    [HttpGet("HistoryItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PagedResult<HistorySyncItemDto>> GetHistoryItems(
        [FromServices] HistorySyncTableManager manager,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? sourceUserId = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50)
    {
        ArgumentNullException.ThrowIfNull(manager);

        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);

        SyncStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<SyncStatus>(status, out var parsedStatus))
        {
            statusFilter = parsedStatus;
        }

        var (items, totalCount) = manager.SearchHistoryItemsPaginated(search, statusFilter, sourceUserId, skip, take);
        var config = _configManager.Configuration;

        return Ok(new PagedResult<HistorySyncItemDto>(
            items.Select(i => i.ToDto(!string.IsNullOrEmpty(config.SourceServerExternalUrl) ? config.SourceServerExternalUrl : config.SourceServerUrl, _configManager.DecryptedSourceServerApiKey)).ToList(),
            totalCount,
            skip,
            take));
    }

    /// <summary>
    /// Gets history sync status counts.
    /// </summary>
    /// <returns>History sync status response with counts.</returns>
    [HttpGet("HistoryStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<BaseSyncStatusResponse> GetHistoryStatus([FromServices] HistorySyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        var counts = manager.GetStatusCounts();

        var response = new BaseSyncStatusResponse
        {
            Pending = counts.GetValueOrDefault(SyncStatus.Pending, 0),
            Queued = counts.GetValueOrDefault(SyncStatus.Queued, 0),
            Synced = counts.GetValueOrDefault(SyncStatus.Synced, 0),
            Errored = counts.GetValueOrDefault(SyncStatus.Errored, 0),
            Ignored = counts.GetValueOrDefault(SyncStatus.Ignored, 0)
        };
        PopulateLastFailure(response, "History");
        return Ok(response);
    }

    /// <summary>
    /// Updates the status of a history sync item.
    /// </summary>
    /// <param name="request">Status update request.</param>
    /// <returns>Action result with success status.</returns>
    [HttpPost("HistoryItems/UpdateStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult UpdateHistoryItemStatus(
        [FromServices] HistorySyncTableManager manager,
        [FromBody] UpdateHistoryItemStatusRequest request)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.TryParse<SyncStatus>(request.Status, out var status))
        {
            return BadRequest("Invalid status value");
        }

        // Prefer database ID if provided
        if (request.Id.HasValue)
        {
            manager.UpdateStatus(request.Id.Value, status);
        }
        else if (!string.IsNullOrEmpty(request.SourceUserId) && !string.IsNullOrEmpty(request.SourceItemId))
        {
            manager.UpdateStatusByKey((request.SourceUserId, request.SourceItemId), status);
        }
        else
        {
            return BadRequest("Either Id or both SourceUserId and SourceItemId must be provided");
        }

        return Ok(new { Success = true });
    }

    /// <summary>
    /// Moves history items to Queued status.
    /// </summary>
    /// <param name="request">Bulk history items request.</param>
    /// <returns>Action result with updated count.</returns>
    [HttpPost("HistoryItems/Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult QueueHistoryItems(
        [FromServices] HistorySyncTableManager manager,
        [FromBody] BulkHistoryItemsRequest request)
    {
        ArgumentNullException.ThrowIfNull(manager);
        // Support both Ids (preferred) and Items (legacy)
        if ((request?.Ids == null || request.Ids.Count == 0) &&
            (request?.Items == null || request.Items.Count == 0))
        {
            return BadRequest("No items specified");
        }

        // Process by database ID if provided (preferred path)
        if (request?.Ids != null && request.Ids.Count > 0)
        {
            try
            {
                var (updated, notFound) = manager.BulkUpdateStatusWithDetails(request.Ids, SyncStatus.Queued);
                return Ok(BuildBulkResult(updated, request.Ids.Count, notFound, "QueueHistoryItems"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to queue history items by IDs");
                return StatusCode(500, new { Error = "Bulk queue failed; see server log" });
            }
        }

        // Legacy fallback by composite key — preserved for older clients.
        var successCount = 0;
        if (request?.Items != null)
        {
            foreach (var item in request.Items)
            {
                try
                {
                    manager.UpdateStatusByKey((item.SourceUserId, item.SourceItemId), SyncStatus.Queued);
                    successCount++;
                }
                catch (Exception ex)
                {
                    var sanitizedUserId = SanitizeForLog(item.SourceUserId);
                    var sanitizedItemId = SanitizeForLog(item.SourceItemId);
                    _logger.LogWarning(ex, "Failed to queue history item {SourceUserId}/{SourceItemId}",
                            sanitizedUserId, sanitizedItemId);
                }
            }
        }

        return Ok(new BulkOperationResult { Updated = successCount, Requested = request?.Items?.Count ?? 0 });
    }

    /// <summary>
    /// Marks history items as ignored.
    /// </summary>
    /// <param name="request">Bulk history items request.</param>
    /// <returns>Action result with updated count.</returns>
    [HttpPost("HistoryItems/Ignore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult IgnoreHistoryItems(
        [FromServices] HistorySyncTableManager manager,
        [FromBody] BulkHistoryItemsRequest request)
    {
        ArgumentNullException.ThrowIfNull(manager);
        // Support both Ids (preferred) and Items (legacy)
        if ((request?.Ids == null || request.Ids.Count == 0) &&
            (request?.Items == null || request.Items.Count == 0))
        {
            return BadRequest("No items specified");
        }

        if (request?.Ids != null && request.Ids.Count > 0)
        {
            try
            {
                var (updated, notFound) = manager.BulkUpdateStatusWithDetails(request.Ids, SyncStatus.Ignored);
                return Ok(BuildBulkResult(updated, request.Ids.Count, notFound, "IgnoreHistoryItems"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ignore history items by IDs");
                return StatusCode(500, new { Error = "Bulk ignore failed; see server log" });
            }
        }

        var successCount = 0;
        if (request?.Items != null)
        {
            foreach (var item in request.Items)
            {
                try
                {
                    manager.UpdateStatusByKey((item.SourceUserId, item.SourceItemId), SyncStatus.Ignored);
                    successCount++;
                }
                catch (Exception ex)
                {
                    var sanitizedUserId = SanitizeForLog(item.SourceUserId);
                    var sanitizedItemId = SanitizeForLog(item.SourceItemId);
                    _logger.LogWarning(ex, "Failed to ignore history item {SourceUserId}/{SourceItemId}",
                            sanitizedUserId, sanitizedItemId);
                }
            }
        }

        return Ok(new BulkOperationResult { Updated = successCount, Requested = request?.Items?.Count ?? 0 });
    }

    /// <summary>
    /// Manually triggers the refresh history sync table task.
    /// </summary>
    /// <returns>Action result with status message.</returns>
    [HttpPost("TriggerHistoryRefresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerHistoryRefresh()
        => ExecuteScheduledTaskByKey("ServerSyncRefreshHistoryTable", "History refresh task started", "History refresh task not found");

    /// <summary>
    /// Manually triggers the sync missing history task.
    /// </summary>
    /// <returns>Action result with status message.</returns>
    [HttpPost("TriggerHistorySync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerHistorySync()
        => ExecuteScheduledTaskByKey("ServerSyncMissingHistory", "History sync task started", "History sync task not found");
}
