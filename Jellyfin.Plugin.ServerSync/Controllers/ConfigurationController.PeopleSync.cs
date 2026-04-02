using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// People sync endpoints for Server Sync plugin.
/// </summary>
public partial class ConfigurationController
{
    // ============================================
    // People Sync Endpoints
    // ============================================

    /// <summary>
    /// Gets a specific people sync item by its database ID.
    /// </summary>
    /// <param name="id">The database ID of the people sync item.</param>
    /// <returns>The people sync item DTO.</returns>
    [HttpGet("PeopleItems/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<PeopleSyncItemDto> GetPeopleSyncItem(long id)
    {
        var item = _databaseProvider.Database.GetPeopleSyncItemById(id);
        if (item == null)
        {
            return NotFound();
        }

        return Ok(MapToPeopleSyncItemDto(item));
    }

    /// <summary>
    /// Gets paginated people sync items with optional search and status filtering.
    /// </summary>
    /// <param name="search">Optional search term to filter by person name.</param>
    /// <param name="status">Optional status filter.</param>
    /// <param name="skip">Number of items to skip.</param>
    /// <param name="take">Number of items to return.</param>
    /// <returns>Paginated people sync items.</returns>
    [HttpGet("PeopleItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PaginatedResult<PeopleSyncItemDto>> GetPeopleSyncItems(
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50)
    {
        // Clamp pagination values
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);

        // Parse status filter
        BaseSyncStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<BaseSyncStatus>(status, out var parsedStatus))
        {
            statusFilter = parsedStatus;
        }

        // Get paginated results
        var (items, totalCount) = _databaseProvider.Database.SearchPeopleSyncItemsPaginated(
            search, statusFilter, skip, take);

        return Ok(new PaginatedResult<PeopleSyncItemDto>
        {
            Items = items.Select(MapToPeopleSyncItemDto).ToList(),
            TotalCount = totalCount,
            Skip = skip,
            Take = take
        });
    }

    /// <summary>
    /// Gets people sync status counts.
    /// </summary>
    /// <returns>People sync status response with counts.</returns>
    [HttpGet("PeopleStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PeopleSyncStatusResponse> GetPeopleSyncStatus()
    {
        var counts = _databaseProvider.Database.GetPeopleSyncStatusCounts();

        return Ok(new PeopleSyncStatusResponse
        {
            Pending = counts.GetValueOrDefault(BaseSyncStatus.Pending, 0),
            Queued = counts.GetValueOrDefault(BaseSyncStatus.Queued, 0),
            Synced = counts.GetValueOrDefault(BaseSyncStatus.Synced, 0),
            Errored = counts.GetValueOrDefault(BaseSyncStatus.Errored, 0),
            Ignored = counts.GetValueOrDefault(BaseSyncStatus.Ignored, 0),
            LastSyncTime = _configManager.Configuration.LastPeopleSyncTime,
            PersonCount = counts.Values.Sum()
        });
    }

    /// <summary>
    /// Updates the status of a people sync item.
    /// </summary>
    /// <param name="request">Status update request.</param>
    /// <returns>Action result with success status.</returns>
    [HttpPost("PeopleItems/UpdateStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult UpdatePeopleSyncItemStatus([FromBody] UpdatePeopleSyncItemStatusRequest request)
    {
        if (!Enum.TryParse<BaseSyncStatus>(request.Status, out var parsedStatus))
        {
            return BadRequest("Invalid status value");
        }

        _databaseProvider.Database.UpdatePeopleSyncItemStatusById(request.Id, parsedStatus);
        return Ok(new { Success = true });
    }

    /// <summary>
    /// Moves people sync items to Queued status.
    /// </summary>
    /// <param name="request">Bulk people sync items request.</param>
    /// <returns>Action result with updated count.</returns>
    [HttpPost("PeopleItems/Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult QueuePeopleSyncItems([FromBody] BulkPeopleSyncItemsRequest request)
    {
        if (request?.Ids == null || request.Ids.Count == 0)
        {
            return BadRequest("No items specified");
        }

        var successCount = 0;

        try
        {
            successCount = _databaseProvider.Database.BatchUpdatePeopleSyncItemStatusByIds(request.Ids, BaseSyncStatus.Queued);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to queue people sync items by IDs");
        }

        return Ok(new { Updated = successCount });
    }

    /// <summary>
    /// Marks people sync items as ignored.
    /// </summary>
    /// <param name="request">Bulk people sync items request.</param>
    /// <returns>Action result with updated count.</returns>
    [HttpPost("PeopleItems/Ignore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult IgnorePeopleSyncItems([FromBody] BulkPeopleSyncItemsRequest request)
    {
        if (request?.Ids == null || request.Ids.Count == 0)
        {
            return BadRequest("No items specified");
        }

        var successCount = 0;

        try
        {
            successCount = _databaseProvider.Database.BatchUpdatePeopleSyncItemStatusByIds(request.Ids, BaseSyncStatus.Ignored);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ignore people sync items by IDs");
        }

        return Ok(new { Updated = successCount });
    }

    /// <summary>
    /// Manually triggers the refresh people sync table task.
    /// </summary>
    /// <returns>Action result with status message.</returns>
    [HttpPost("TriggerPeopleRefresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerPeopleRefresh()
    {
        var refreshTask = _taskManager.ScheduledTasks
            .FirstOrDefault(t => t.ScheduledTask.Key == "ServerSyncRefreshPeopleTable");

        if (refreshTask == null)
        {
            return NotFound("People refresh task not found");
        }

        _taskManager.Execute(refreshTask, new TaskOptions());

        return Ok(new { Message = "People refresh task started" });
    }

    /// <summary>
    /// Manually triggers the sync missing people task.
    /// </summary>
    /// <returns>Action result with status message.</returns>
    [HttpPost("TriggerPeopleSync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerPeopleSync()
    {
        var syncTask = _taskManager.ScheduledTasks
            .FirstOrDefault(t => t.ScheduledTask.Key == "ServerSyncMissingPeople");

        if (syncTask == null)
        {
            return NotFound("People sync task not found");
        }

        _taskManager.Execute(syncTask, new TaskOptions());

        return Ok(new { Message = "People sync task started" });
    }

    /// <summary>
    /// Resets the people sync database, removing all tracked people sync items.
    /// </summary>
    /// <returns>Action result with success status.</returns>
    [HttpPost("ResetPeopleSyncDatabase")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ResetPeopleSyncDatabase()
    {
        try
        {
            _databaseProvider.Database.ResetPeopleSyncDatabase();
            _logger.LogInformation("People sync database has been reset");
            return Ok(new { Success = true, Message = "People sync database reset successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset people sync database");
            return StatusCode(500, new { Success = false, Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Maps a PeopleSyncItem to a DTO.
    /// </summary>
    private PeopleSyncItemDto MapToPeopleSyncItemDto(PeopleSyncItem item)
    {
        var config = _configManager.Configuration;
        return new PeopleSyncItemDto
        {
            Id = item.Id,
            PersonName = item.PersonName,
            SourcePersonId = item.SourcePersonId,
            LocalPersonId = item.LocalPersonId,
            SourceOverview = item.SourceOverview,
            LocalOverview = item.LocalOverview,
            SourceProviderIds = item.SourceProviderIds,
            LocalProviderIds = item.LocalProviderIds,
            HasOverviewChanges = item.HasOverviewChanges,
            HasProviderIdChanges = item.HasProviderIdChanges,
            HasImagesChanges = item.HasImagesChanges,
            HasChanges = item.HasChanges,
            Status = item.Status.ToString(),
            StatusDate = item.StatusDate,
            LastSyncTime = item.LastSyncTime,
            ErrorMessage = item.ErrorMessage,
            SourceServerUrl = !string.IsNullOrEmpty(config.SourceServerExternalUrl)
                ? config.SourceServerExternalUrl
                : config.SourceServerUrl,
            SourceServerApiKey = config.SourceServerApiKey
        };
    }
}
