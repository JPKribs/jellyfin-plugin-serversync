using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// Metadata sync endpoints for Server Sync plugin.
/// </summary>
public partial class ConfigurationController
{
    // ============================================
    // Metadata Sync Endpoints
    // ============================================

    /// <summary>
    /// Gets a specific metadata sync item by its ID.
    /// </summary>
    /// <param name="id">The database ID of the metadata sync item.</param>
    /// <returns>The metadata sync item DTO.</returns>
    [HttpGet("MetadataItems/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MetadataSyncItemDto>> GetMetadataSyncItem(
        long id,
        [FromServices] MetadataSyncTableManager manager,
        [FromServices] MetadataSyncTableService metadataService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(metadataService);
        var item = manager.GetById(id);
        if (item == null)
        {
            return NotFound();
        }

        var config = _configManager.Configuration;

        // Re-read live local state so the modal reflects what's actually on
        // the server, not the snapshot from the last metadata Refresh.
        metadataService.RefreshLocalSnapshot(
            item,
            syncMetadata: config.MetadataSyncMetadata,
            syncImages: config.MetadataSyncImages,
            syncPeople: config.MetadataSyncPeople,
            syncStudios: config.MetadataSyncStudios,
            syncGenres: config.MetadataSyncGenres,
            syncTags: config.MetadataSyncTags);

        // Refresh builds tag-only manifests for speed; the modal needs real
        // sizes/dimensions to render "623.4 KB" instead of "1 (0 B)".
        if (config.MetadataSyncImages
            && !string.IsNullOrWhiteSpace(config.SourceServerUrl)
            && !string.IsNullOrWhiteSpace(config.SourceServerApiKey)
            && !string.IsNullOrEmpty(item.Images.Source))
        {
            try
            {
                using var client = _clientFactory.Create(config.SourceServerUrl, config.SourceServerApiKey);
                await metadataService.EnrichSourceImageSizesAsync(item, client, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Source image enrichment failed for metadata item {Id} ({Name}); modal will show tag-only sizes", id, item.ItemName);
            }
        }

        return Ok(item.ToDto(config.LibraryMappings, !string.IsNullOrEmpty(config.SourceServerExternalUrl) ? config.SourceServerExternalUrl : config.SourceServerUrl));
    }

    /// <summary>
    /// Gets image info (including sizes) for a metadata sync item from the source server.
    /// </summary>
    /// <param name="id">The database ID of the metadata sync item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of image info with sizes.</returns>
    [HttpGet("MetadataItems/{id}/ImageInfo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<ImageInfoDto>>> GetMetadataItemImageInfo(
        long id,
        [FromServices] MetadataSyncTableManager manager,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manager);
        var item = manager.GetById(id);
        if (item == null || string.IsNullOrEmpty(item.SourceItemId))
        {
            return NotFound();
        }

        var config = _configManager.Configuration;
        if (string.IsNullOrEmpty(config.SourceServerUrl) || string.IsNullOrEmpty(config.SourceServerApiKey))
        {
            return NotFound("Source server not configured");
        }

        try
        {
            using var client = _clientFactory.Create(config.SourceServerUrl, config.SourceServerApiKey);

            if (!Guid.TryParse(item.SourceItemId, out var sourceItemGuid))
            {
                return NotFound("Invalid source item ID");
            }

            var imageInfoList = await client.GetItemImageInfoAsync(sourceItemGuid, cancellationToken).ConfigureAwait(false);
            if (imageInfoList == null)
            {
                return Ok(new List<ImageInfoDto>());
            }

            var result = imageInfoList.Select(img => new ImageInfoDto
            {
                ImageType = img.ImageType?.ToString() ?? "Unknown",
                ImageIndex = img.ImageIndex ?? 0,
                Size = img.Size ?? 0,
                Width = img.Width ?? 0,
                Height = img.Height ?? 0
            }).ToList();

            return Ok(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch image info for metadata item {Id}", id);
            return Ok(new List<ImageInfoDto>());
        }
    }

    /// <summary>
    /// Gets paginated metadata sync items from the database with optional search and filter.
    /// One record per item containing all categories (Metadata, Images, People).
    /// </summary>
    /// <param name="search">Optional search term (matches item names or paths).</param>
    /// <param name="status">Optional status filter.</param>
    /// <param name="sourceLibraryId">Optional source library ID filter.</param>
    /// <param name="skip">Number of items to skip (default 0).</param>
    /// <param name="take">Maximum items to return (default 50, max 200).</param>
    /// <returns>Paginated result of metadata sync item DTOs.</returns>
    [HttpGet("MetadataItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PagedResult<MetadataSyncItemDto>> GetMetadataSyncItems(
        [FromServices] MetadataSyncTableManager manager,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? sourceLibraryId = null,
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

        var (items, totalCount) = manager.SearchMetadataSyncItemsPaginated(
            search, statusFilter, sourceLibraryId, skip, take);

        var config = _configManager.Configuration;

        return Ok(new PagedResult<MetadataSyncItemDto>(
            items.Select(i => i.ToDto(
                config.LibraryMappings,
                !string.IsNullOrEmpty(config.SourceServerExternalUrl) ? config.SourceServerExternalUrl : config.SourceServerUrl,
                includeBlobs: false)).ToList(),
            totalCount,
            skip,
            take));
    }

    /// <summary>
    /// Gets metadata sync status counts.
    /// </summary>
    /// <returns>Metadata sync status response with counts.</returns>
    [HttpGet("MetadataStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<MetadataSyncStatusResponse> GetMetadataSyncStatus([FromServices] MetadataSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        var counts = manager.GetStatusCounts();
        var libraryMappings = _configManager.Configuration.LibraryMappings ?? new List<LibraryMapping>();

        var response = new MetadataSyncStatusResponse
        {
            Pending = counts.GetValueOrDefault(SyncStatus.Pending, 0),
            Queued = counts.GetValueOrDefault(SyncStatus.Queued, 0),
            Synced = counts.GetValueOrDefault(SyncStatus.Synced, 0),
            Errored = counts.GetValueOrDefault(SyncStatus.Errored, 0),
            Ignored = counts.GetValueOrDefault(SyncStatus.Ignored, 0),
            LastSyncTime = _configManager.Configuration.LastMetadataSyncTime,
            LibraryCount = libraryMappings.Count
        };
        PopulateLastFailure(response, "Metadata");
        return Ok(response);
    }

    /// <summary>
    /// Updates the status of a metadata sync item.
    /// </summary>
    /// <param name="request">Status update request.</param>
    /// <returns>Action result with success status.</returns>
    [HttpPost("MetadataItems/UpdateStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult UpdateMetadataSyncItemStatus(
        [FromServices] MetadataSyncTableManager manager,
        [FromBody] UpdateMetadataSyncItemStatusRequest request)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.TryParse<SyncStatus>(request.Status, out var status) || !Enum.IsDefined(status))
        {
            return BadRequest("Invalid status value");
        }

        manager.UpdateStatus(request.Id, status);
        return Ok(new { Success = true });
    }

    /// <summary>
    /// Moves metadata sync items to Queued status.
    /// </summary>
    /// <param name="request">Bulk metadata sync items request.</param>
    /// <returns>Action result with updated count.</returns>
    [HttpPost("MetadataItems/Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult QueueMetadataSyncItems(
        [FromServices] MetadataSyncTableManager manager,
        [FromBody] BulkMetadataSyncItemsRequest request)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (request?.Ids == null || request.Ids.Count == 0)
        {
            return BadRequest("No items specified");
        }

        try
        {
            var (updated, notFound) = manager.BatchUpdateStatusByIdsWithDetails(request.Ids, SyncStatus.Queued);
            return Ok(BuildBulkResult(updated, request.Ids.Count, notFound, "QueueMetadataSyncItems"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue metadata sync items by IDs");
            return StatusCode(500, new { Error = "Bulk queue failed; see server log" });
        }
    }

    /// <summary>
    /// Marks metadata sync items as ignored.
    /// </summary>
    /// <param name="request">Bulk metadata sync items request.</param>
    /// <returns>Action result with updated count.</returns>
    [HttpPost("MetadataItems/Ignore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult IgnoreMetadataSyncItems(
        [FromServices] MetadataSyncTableManager manager,
        [FromBody] BulkMetadataSyncItemsRequest request)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (request?.Ids == null || request.Ids.Count == 0)
        {
            return BadRequest("No items specified");
        }

        try
        {
            var (updated, notFound) = manager.BatchUpdateStatusByIdsWithDetails(request.Ids, SyncStatus.Ignored);
            return Ok(BuildBulkResult(updated, request.Ids.Count, notFound, "IgnoreMetadataSyncItems"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ignore metadata sync items by IDs");
            return StatusCode(500, new { Error = "Bulk ignore failed; see server log" });
        }
    }

    /// <summary>
    /// Manually triggers the refresh metadata sync table task.
    /// </summary>
    /// <returns>Action result with status message.</returns>
    [HttpPost("TriggerMetadataRefresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerMetadataRefresh()
        => ExecuteScheduledTaskByKey("ServerSyncRefreshMetadataTable", "Metadata refresh task started", "Metadata refresh task not found");

    /// <summary>
    /// Manually triggers the sync missing metadata task.
    /// </summary>
    /// <returns>Action result with status message.</returns>
    [HttpPost("TriggerMetadataSync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerMetadataSync()
        => ExecuteScheduledTaskByKey("ServerSyncMissingMetadata", "Metadata sync task started", "Metadata sync task not found");

    /// <summary>
    /// Resets the metadata sync database, removing all tracked metadata sync items.
    /// </summary>
    /// <returns>Action result with success status.</returns>
    [HttpPost("ResetMetadataSyncDatabase")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ResetMetadataSyncDatabase([FromServices] MetadataSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        try
        {
            var deleted = manager.ResetTable();
            _logger.LogInformation("Metadata sync database has been reset, {Count} rows deleted", deleted);
            return Ok(new { Success = true, Message = "Metadata sync database reset successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset metadata sync database");
            return StatusCode(500, new { Success = false, Error = "An internal error occurred. Check server logs for details." });
        }
    }
}
