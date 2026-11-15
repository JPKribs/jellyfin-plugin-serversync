using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Extensions;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.HistorySync;
using JPKribs.Jellyfin.Base;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// The History routes of the Sync tab.
/// </summary>
public partial class ConfigurationController
{
    /// <summary>
    /// Lists a page of History rows, narrowed by search, status, and source user.
    /// </summary>
    /// <param name="manager">Reads and writes the History sync table.</param>
    /// <param name="search">Narrows the list to names that match, when given.</param>
    /// <param name="status">Narrows the list to one status, when given.</param>
    /// <param name="sourceUserId">Narrows the list to one user on the source, when given.</param>
    /// <param name="skip">How many rows to pass over before the page starts.</param>
    /// <param name="take">The most rows the page holds, clamped to 200.</param>
    /// <returns>A page of rows with the total count.</returns>
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
    /// Counts the History rows in each status.
    /// </summary>
    /// <param name="manager">Reads and writes the History sync table.</param>
    /// <returns>The counts, with the last run failure when there is one.</returns>
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
    /// Queues the selected History rows. With no IDs and a status, it queues every row in that status, which is how a retry reaches every errored row.
    /// </summary>
    /// <param name="manager">Reads and writes the History sync table.</param>
    /// <param name="request">The selected rows, by ID or by user and item.</param>
    /// <returns>How many rows changed and which did not, or 400 when none was selected.</returns>
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
    /// Marks the selected History rows as Ignored.
    /// </summary>
    /// <param name="manager">Reads and writes the History sync table.</param>
    /// <param name="request">The selected rows, by ID or by user and item.</param>
    /// <returns>How many rows changed and which did not, or 400 when none was selected.</returns>
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
    /// Sets the status of the selected History rows, named by ID or by user and item.
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
            // Any failure becomes a 500 the page can show.
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
            // One row's failure must not stop the rest.
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to {Verb} history item {SourceUserId}/{SourceItemId}",
                    verb, SanitizeForLog(item.SourceUserId), SanitizeForLog(item.SourceItemId));
            }
        }

        return Ok(new BulkOperationResult { Updated = successCount, Requested = request.Items.Count });
    }
}
