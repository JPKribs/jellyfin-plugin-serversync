using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services;
using JPKribs.Jellyfin.Base;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// People sync endpoints for Server Sync plugin.
/// </summary>
public partial class ConfigurationController
{
    /// <summary>
    /// Gets a specific people sync item by its database ID.
    /// </summary>
    /// <param name="id">The row id.</param>
    /// <param name="manager">Reads and writes the sync table.</param>
    /// <param name="peopleService">Builds and compares people sync rows.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The row, or 404 when there is none.</returns>
    [HttpGet("PeopleItems/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PeopleSyncItemDto>> GetPeopleSyncItem(
        long id,
        [FromServices] PeopleSyncTableManager manager,
        [FromServices] PeopleSyncTableService peopleService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(peopleService);
        var item = manager.GetById(id);
        if (item == null)
        {
            return NotFound();
        }

        var config = _configManager.Configuration;

        // Re-read live local state so the modal shows what's actually in
        // Jellyfin right now, not the snapshot from the last People Refresh.
        // Without this, a successful Sync apply doesn't show up here until
        // the user re-runs the Refresh task.
        peopleService.RefreshLocalSnapshot(item, syncImages: config.PeopleSyncImages);

        // Refresh builds source manifests from ImageTags only (no per-person
        // HTTP call) for speed. The modal needs real sizes/dimensions to
        // render Source as e.g. "623.4 KB" instead of "1 (0 B)".
        if (config.PeopleSyncImages && !string.IsNullOrEmpty(item.Images.Source))
        {
            try
            {
                using var client = ClientForRow(item.ServerKey);
                if (client != null)
                {
                    await peopleService.EnrichSourceImageSizesAsync(item, client, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Source image enrichment failed for person {Name}. Modal will show tag-only sizes", item.PersonName);
            }
        }

        return Ok(item.ToDto(BrowserUrlFor(item.ServerKey)));
    }

    /// <summary>
    /// Gets paginated people sync items with optional search and status filtering.
    /// </summary>
    /// <param name="manager">Reads and writes the sync table.</param>
    /// <param name="search">Text to find in the item names, or null for no filter.</param>
    /// <param name="status">The item status to list, or null for every status.</param>
    /// <param name="skip">How many rows to skip.</param>
    /// <param name="take">How many rows to return.</param>
    /// <returns>The requested page of rows.</returns>
    [HttpGet("PeopleItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PagedResult<PeopleSyncItemDto>> GetPeopleSyncItems(
        [FromServices] PeopleSyncTableManager manager,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
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

        // Page by offset. Converting skip to a page number landed on the wrong rows whenever skip was
        // not a multiple of take.
        var result = manager.SearchPaginated(search, statusFilter, skip, take);

        return Ok(new PagedResult<PeopleSyncItemDto>(
            result.Items.Select(i => i.ToDto(BrowserUrlFor(i.ServerKey))).ToList(),
            result.TotalCount,
            skip,
            take));
    }

    /// <summary>
    /// Gets people sync status counts.
    /// </summary>
    /// <param name="manager">Reads and writes the sync table.</param>
    /// <returns>The row counts by status.</returns>
    [HttpGet("PeopleStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PeopleSyncStatusResponse> GetPeopleSyncStatus([FromServices] PeopleSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        var response = new PeopleSyncStatusResponse
        {
            Pending = manager.CountByStatus(SyncStatus.Pending),
            Queued = manager.CountByStatus(SyncStatus.Queued),
            Synced = manager.CountByStatus(SyncStatus.Synced),
            Errored = manager.CountByStatus(SyncStatus.Errored),
            Ignored = manager.CountByStatus(SyncStatus.Ignored),
            LastSyncTime = _configManager.Configuration.LastPeopleSyncTime,
            PersonCount = manager.Count()
        };
        PopulateLastFailure(response, "People");
        return Ok(response);
    }

    /// <summary>
    /// Moves people sync items to Queued status. With no ids and a Status, queues every row in that
    /// status, which is how "Retry errors" reaches every errored row.
    /// </summary>
    /// <param name="request">The request body.</param>
    /// <param name="manager">Reads and writes the sync table.</param>
    /// <returns>The result of the bulk change.</returns>
    [HttpPost("PeopleItems/Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult QueuePeopleSyncItems(
        [FromBody] BulkPeopleSyncItemsRequest request,
        [FromServices] PeopleSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (request != null && (request.Ids == null || request.Ids.Count == 0) && !string.IsNullOrEmpty(request.Status))
        {
            return QueueAllWithStatus(manager, request.Status, "QueuePeopleSyncItems");
        }

        if (request?.Ids == null || request.Ids.Count == 0)
        {
            return BadRequest("Select the items.");
        }

        try
        {
            var (updated, notFound) = manager.BulkUpdateStatusWithDetails(request.Ids, SyncStatus.Queued);
            return Ok(BuildBulkResult(updated, request.Ids.Count, notFound, "QueuePeopleSyncItems"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue people sync items by IDs");
            return StatusCode(500, new { Error = "Could not queue the items. See the server log." });
        }
    }

    /// <summary>
    /// Marks people sync items as ignored.
    /// </summary>
    /// <param name="request">The request body.</param>
    /// <param name="manager">Reads and writes the sync table.</param>
    /// <returns>The result of the bulk change.</returns>
    [HttpPost("PeopleItems/Ignore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult IgnorePeopleSyncItems(
        [FromBody] BulkPeopleSyncItemsRequest request,
        [FromServices] PeopleSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (request?.Ids == null || request.Ids.Count == 0)
        {
            return BadRequest("Select the items.");
        }

        try
        {
            var (updated, notFound) = manager.BulkUpdateStatusWithDetails(request.Ids, SyncStatus.Ignored);
            return Ok(BuildBulkResult(updated, request.Ids.Count, notFound, "IgnorePeopleSyncItems"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ignore people sync items by IDs");
            return StatusCode(500, new { Error = "Could not ignore the items. See the server log." });
        }
    }
}
