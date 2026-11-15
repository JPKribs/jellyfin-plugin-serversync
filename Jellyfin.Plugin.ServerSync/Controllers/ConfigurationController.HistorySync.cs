using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Services;
using JPKribs.Jellyfin.Base;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// History sync endpoints for Server Sync plugin.
/// </summary>
public partial class ConfigurationController
{
    /// <summary>
    /// Gets paginated history sync items from the database with optional search and filter.
    /// </summary>
    /// <param name="manager">Reads and writes the sync table.</param>
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
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<SyncStatus>(status, out var parsedStatus) && Enum.IsDefined(parsedStatus))
        {
            statusFilter = parsedStatus;
        }

        var (items, totalCount) = manager.SearchHistoryItemsPaginated(search, statusFilter, sourceUserId, skip, take);

        return Ok(new PagedResult<HistorySyncItemDto>(
            items.Select(i => i.ToDto(BrowserUrlFor(i.ServerKey))).ToList(),
            totalCount,
            skip,
            take));
    }

    /// <summary>
    /// Gets history sync status counts.
    /// </summary>
    /// <param name="manager">Reads and writes the sync table.</param>
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
    /// Moves history items to Queued status. With no ids and a Status, queues every row in that
    /// status, which is how "Retry errors" reaches every errored row.
    /// </summary>
    /// <param name="manager">Reads and writes the sync table.</param>
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
        if (request != null && (request.Ids == null || request.Ids.Count == 0) && !string.IsNullOrEmpty(request.Status))
        {
            return QueueAllWithStatus(manager, request.Status, "QueueHistoryItems");
        }

        return UpdateHistoryItemsStatus(manager, request, SyncStatus.Queued, "QueueHistoryItems", "queue");
    }

    /// <summary>
    /// Marks history items as ignored.
    /// </summary>
    /// <param name="manager">Reads and writes the sync table.</param>
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
        return UpdateHistoryItemsStatus(manager, request, SyncStatus.Ignored, "IgnoreHistoryItems", "ignore");
    }

    /// <summary>
    /// The shared body of the history Queue and Ignore endpoints. Ids are the preferred way to name
    /// rows. The composite key list is kept for older clients.
    /// </summary>
    private ActionResult UpdateHistoryItemsStatus(
        HistorySyncTableManager manager,
        BulkHistoryItemsRequest? request,
        SyncStatus status,
        string operation,
        string verb)
    {
        var hasIds = request?.Ids is { Count: > 0 };
        var hasItems = request?.Items is { Count: > 0 };
        if (request is null || (!hasIds && !hasItems))
        {
            return BadRequest("Select the items.");
        }

        if (hasIds)
        {
            try
            {
                var (updated, notFound) = manager.BulkUpdateStatusWithDetails(request.Ids, status);
                return Ok(BuildBulkResult(updated, request.Ids.Count, notFound, operation));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to {Verb} history items by IDs", verb);
                return StatusCode(500, new { Error = $"Could not {verb} the items. See the server log." });
            }
        }

        var successCount = 0;
        foreach (var item in request.Items)
        {
            try
            {
                manager.UpdateStatusByKey((item.SourceUserId, item.SourceItemId), status);
                successCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to {Verb} history item {SourceUserId}/{SourceItemId}",
                    verb, SanitizeForLog(item.SourceUserId), SanitizeForLog(item.SourceItemId));
            }
        }

        return Ok(new BulkOperationResult { Updated = successCount, Requested = request.Items.Count });
    }
}
