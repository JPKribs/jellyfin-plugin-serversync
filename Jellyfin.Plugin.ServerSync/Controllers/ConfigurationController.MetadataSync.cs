using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Extensions;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using JPKribs.Jellyfin.Base;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// Metadata sync endpoints for Server Sync plugin.
/// </summary>
public partial class ConfigurationController
{
    /// <summary>
    /// Gets a specific metadata sync item by its ID.
    /// </summary>
    /// <param name="id">The database ID of the metadata sync item.</param>
    /// <param name="manager">Reads and writes the sync table.</param>
    /// <param name="metadataService">Builds and compares metadata sync rows.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
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

        // Refresh builds tag-only manifests for speed. The modal needs real
        // sizes/dimensions to render "623.4 KB" instead of "1 (0 B)".
        if (config.MetadataSyncImages && !string.IsNullOrEmpty(item.Images.Source))
        {
            try
            {
                using var client = ClientForRow(item.ServerKey);
                if (client != null)
                {
                    await metadataService.EnrichSourceImageSizesAsync(item, client, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Source image enrichment failed for metadata item {Id} ({Name}). Modal will show tag-only sizes", id, item.ItemName);
            }
        }

        return Ok(item.ToDto(config.GetAllLibraryMappings(), BrowserUrlFor(item.ServerKey)));
    }

    /// <summary>
    /// Gets paginated metadata sync items from the database with optional search and filter.
    /// One record per item containing all categories (Metadata, Images, People).
    /// </summary>
    /// <param name="manager">Reads and writes the sync table.</param>
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

        var libraryMappings = _configManager.Configuration.GetAllLibraryMappings();

        return Ok(new PagedResult<MetadataSyncItemDto>(
            items.Select(i => i.ToDto(
                libraryMappings,
                BrowserUrlFor(i.ServerKey),
                includeBlobs: false)).ToList(),
            totalCount,
            skip,
            take));
    }

    /// <summary>
    /// Gets metadata sync status counts.
    /// </summary>
    /// <param name="manager">Reads and writes the sync table.</param>
    /// <returns>Metadata sync status response with counts.</returns>
    [HttpGet("MetadataStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<MetadataSyncStatusResponse> GetMetadataSyncStatus([FromServices] MetadataSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        var counts = manager.GetStatusCounts();
        var libraryMappings = _configManager.Configuration.GetAllLibraryMappings();

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
    /// Moves metadata sync items to Queued status. With no ids and a Status, queues every row in that
    /// status, which is how "Retry errors" reaches every errored row.
    /// </summary>
    /// <param name="manager">Reads and writes the sync table.</param>
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
        if (request != null && (request.Ids == null || request.Ids.Count == 0) && !string.IsNullOrEmpty(request.Status))
        {
            return QueueAllWithStatus(manager, request.Status, "QueueMetadataSyncItems");
        }

        if (request?.Ids == null || request.Ids.Count == 0)
        {
            return BadRequest("Select the items.");
        }

        try
        {
            var (updated, notFound) = manager.BatchUpdateStatusByIdsWithDetails(request.Ids, SyncStatus.Queued);
            return Ok(BuildBulkResult(updated, request.Ids.Count, notFound, "QueueMetadataSyncItems"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue metadata sync items by IDs");
            return StatusCode(500, new { Error = "Could not queue the items. See the server log." });
        }
    }

    /// <summary>
    /// Marks metadata sync items as ignored.
    /// </summary>
    /// <param name="manager">Reads and writes the sync table.</param>
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
            return BadRequest("Select the items.");
        }

        try
        {
            var (updated, notFound) = manager.BatchUpdateStatusByIdsWithDetails(request.Ids, SyncStatus.Ignored);
            return Ok(BuildBulkResult(updated, request.Ids.Count, notFound, "IgnoreMetadataSyncItems"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ignore metadata sync items by IDs");
            return StatusCode(500, new { Error = "Could not ignore the items. See the server log." });
        }
    }
}
