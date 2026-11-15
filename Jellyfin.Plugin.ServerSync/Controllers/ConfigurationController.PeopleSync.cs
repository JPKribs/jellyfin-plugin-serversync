using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Extensions;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services.PeopleSync;
using JPKribs.Jellyfin.Base;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// The People routes of the Sync tab.
/// </summary>
public partial class ConfigurationController
{
    /// <summary>
    /// Reads one People row with this server's live values and the source's measured image sizes.
    /// </summary>
    /// <param name="id">The row's ID in the People sync table.</param>
    /// <param name="manager">Reads and writes the People sync table.</param>
    /// <param name="peopleService">Reads the live local values and measures the source's images.</param>
    /// <param name="cancellationToken">Cancels the calls to the source.</param>
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

        peopleService.RefreshLocalSnapshot(item, syncImages: config.PeopleSyncImages);

        // A scan stores tags only, and the dialog needs real sizes.
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Source image enrichment failed for person {Name}. Modal will show tag-only sizes", SanitizeForLog(item.PersonName));
            }
        }

        return Ok(item.ToDto(BrowserUrlFor(item.ServerKey)));
    }

    /// <summary>
    /// Lists a page of People rows, narrowed by search and status.
    /// </summary>
    /// <param name="manager">Reads and writes the People sync table.</param>
    /// <param name="search">Text to find in the item names, or null for no filter.</param>
    /// <param name="status">The item status to list, or null for every status.</param>
    /// <param name="skip">How many rows to pass over before the page starts.</param>
    /// <param name="take">The most rows the page holds, clamped to 200.</param>
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

        var result = manager.SearchPaginated(search, statusFilter, skip, take);

        return Ok(new PagedResult<PeopleSyncItemDto>(
            result.Items.Select(i => i.ToDto(BrowserUrlFor(i.ServerKey))).ToList(),
            result.TotalCount,
            skip,
            take));
    }

    /// <summary>
    /// Counts the People rows in each status.
    /// </summary>
    /// <param name="manager">Reads and writes the People sync table.</param>
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
    /// Queues the selected People rows. With no IDs and a status, it queues every row in that status, which is how a retry reaches every errored row.
    /// </summary>
    /// <param name="request">The IDs of the selected rows.</param>
    /// <param name="manager">Reads and writes the People sync table.</param>
    /// <returns>How many rows changed and which did not, or 400 when none was selected.</returns>
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
        // Any failure becomes a 500 the page can show.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue people sync items by IDs");
            return StatusCode(500, new { Error = "Could not queue the items. See the server log." });
        }
    }

    /// <summary>
    /// Marks the selected People rows as Ignored.
    /// </summary>
    /// <param name="request">The IDs of the selected rows.</param>
    /// <param name="manager">Reads and writes the People sync table.</param>
    /// <returns>How many rows changed and which did not, or 400 when none was selected.</returns>
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
        // Any failure becomes a 500 the page can show.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ignore people sync items by IDs");
            return StatusCode(500, new { Error = "Could not ignore the items. See the server log." });
        }
    }
}
