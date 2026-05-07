using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services;
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
    /// <summary>
    /// Gets a specific people sync item by its database ID.
    /// </summary>
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

        // Enrich source-side image manifest with real sizes/dimensions so
        // the modal renders Source as e.g. "623.4 KB" instead of "1 (0 B)".
        // The refresh builds source manifests from ImageTags only (no per-
        // person HTTP call) for performance; this is the per-modal-open
        // compensation.
        if (config.PeopleSyncImages
            && !string.IsNullOrWhiteSpace(config.SourceServerUrl)
            && !string.IsNullOrWhiteSpace(config.SourceServerApiKey)
            && !string.IsNullOrEmpty(item.Images.Source))
        {
            try
            {
                using var client = _clientFactory.Create(config.SourceServerUrl, config.SourceServerApiKey);
                await peopleService.EnrichSourceImageSizesAsync(item, client, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Source image enrichment failed for person {Name}; modal will show tag-only sizes", item.PersonName);
            }
        }

        return Ok(MapToPeopleSyncItemDto(item));
    }

    /// <summary>
    /// Gets paginated people sync items with optional search and status filtering.
    /// </summary>
    [HttpGet("PeopleItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PaginatedResult<PeopleSyncItemDto>> GetPeopleSyncItems(
        [FromServices] PeopleSyncTableManager manager,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50)
    {
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);

        SyncStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<SyncStatus>(status, out var parsedStatus))
        {
            statusFilter = parsedStatus;
        }

        var page = (skip / Math.Max(1, take)) + 1;
        var result = manager.Paginate(new PaginationRequest
        {
            Page = page,
            PageSize = take,
            SearchTerm = search,
            StatusFilter = statusFilter
        });

        return Ok(new PaginatedResult<PeopleSyncItemDto>
        {
            Items = result.Items.Select(MapToPeopleSyncItemDto).ToList(),
            TotalCount = result.TotalCount,
            Skip = skip,
            Take = take
        });
    }

    /// <summary>
    /// Gets people sync status counts.
    /// </summary>
    [HttpGet("PeopleStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PeopleSyncStatusResponse> GetPeopleSyncStatus([FromServices] PeopleSyncTableManager manager)
    {
        return Ok(new PeopleSyncStatusResponse
        {
            Pending = manager.CountByStatus(SyncStatus.Pending),
            Queued = manager.CountByStatus(SyncStatus.Queued),
            Synced = manager.CountByStatus(SyncStatus.Synced),
            Errored = manager.CountByStatus(SyncStatus.Errored),
            Ignored = manager.CountByStatus(SyncStatus.Ignored),
            LastSyncTime = _configManager.Configuration.LastPeopleSyncTime,
            PersonCount = manager.Count()
        });
    }

    /// <summary>
    /// Updates the status of a people sync item.
    /// </summary>
    [HttpPost("PeopleItems/UpdateStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult UpdatePeopleSyncItemStatus(
        [FromBody] UpdatePeopleSyncItemStatusRequest request,
        [FromServices] PeopleSyncTableManager manager)
    {
        if (!Enum.TryParse<SyncStatus>(request.Status, out var parsedStatus))
        {
            return BadRequest("Invalid status value");
        }

        manager.UpdateStatus(request.Id, parsedStatus);
        return Ok(new { Success = true });
    }

    /// <summary>
    /// Moves people sync items to Queued status.
    /// </summary>
    [HttpPost("PeopleItems/Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult QueuePeopleSyncItems(
        [FromBody] BulkPeopleSyncItemsRequest request,
        [FromServices] PeopleSyncTableManager manager)
    {
        if (request?.Ids == null || request.Ids.Count == 0)
        {
            return BadRequest("No items specified");
        }

        var successCount = 0;
        try
        {
            successCount = manager.BulkUpdateStatus(request.Ids, SyncStatus.Queued);
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
    [HttpPost("PeopleItems/Ignore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult IgnorePeopleSyncItems(
        [FromBody] BulkPeopleSyncItemsRequest request,
        [FromServices] PeopleSyncTableManager manager)
    {
        if (request?.Ids == null || request.Ids.Count == 0)
        {
            return BadRequest("No items specified");
        }

        var successCount = 0;
        try
        {
            successCount = manager.BulkUpdateStatus(request.Ids, SyncStatus.Ignored);
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
    [HttpPost("TriggerPeopleRefresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerPeopleRefresh()
        => ExecuteScheduledTaskByKey("ServerSyncRefreshPeopleTable", "People refresh task started", "People refresh task not found");

    /// <summary>
    /// Manually triggers the sync missing people task.
    /// </summary>
    [HttpPost("TriggerPeopleSync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerPeopleSync()
        => ExecuteScheduledTaskByKey("ServerSyncMissingPeople", "People sync task started", "People sync task not found");

    /// <summary>
    /// Resets the people sync database, removing all tracked people sync items.
    /// </summary>
    [HttpPost("ResetPeopleSyncDatabase")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ResetPeopleSyncDatabase([FromServices] PeopleSyncTableManager manager)
    {
        try
        {
            var deleted = manager.ResetTable();
            _logger.LogInformation("People sync database has been reset, {Count} rows deleted", deleted);
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
            SourceMetadataValue = item.Metadata.Source,
            LocalMetadataValue = item.Metadata.Local,
            SourceImagesValue = item.Images.Source,
            LocalImagesValue = item.Images.Local,
            HasMetadataChanges = item.HasMetadataChanges,
            HasImagesChanges = item.HasImagesChanges,
            HasChanges = item.HasChanges,
            Status = item.Status.ToString(),
            StatusDate = item.StatusDate,
            LastSyncTime = item.LastSyncTime,
            ErrorMessage = item.Reason,
            SourceServerUrl = !string.IsNullOrEmpty(config.SourceServerExternalUrl)
                ? config.SourceServerExternalUrl
                : config.SourceServerUrl,
            SourceServerApiKey = config.SourceServerApiKey
        };
    }
}
