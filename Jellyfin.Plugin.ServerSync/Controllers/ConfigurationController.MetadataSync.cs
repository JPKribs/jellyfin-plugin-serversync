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
/// The Metadata routes of the Sync tab.
/// </summary>
public partial class ConfigurationController
{
    /// <summary>
    /// Reads one Metadata row with this server's live values and the source's measured image sizes.
    /// </summary>
    /// <param name="id">The row's ID in the Metadata sync table.</param>
    /// <param name="manager">Reads and writes the Metadata sync table.</param>
    /// <param name="metadataService">Reads the live local values and measures the source's images.</param>
    /// <param name="cancellationToken">Cancels the calls to the source.</param>
    /// <returns>The row, or 404 when there is none.</returns>
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

        metadataService.RefreshLocalSnapshot(
            item,
            syncMetadata: config.MetadataSyncMetadata,
            syncImages: config.MetadataSyncImages,
            syncPeople: config.MetadataSyncPeople,
            syncStudios: config.MetadataSyncStudios,
            syncGenres: config.MetadataSyncGenres,
            syncTags: config.MetadataSyncTags);

        // A scan stores tags only, and the dialog needs real sizes.
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Source image enrichment failed for metadata item {Id} ({Name}). Modal will show tag-only sizes", id, SanitizeForLog(item.ItemName));
            }
        }

        return Ok(item.ToDto(config.GetAllLibraryMappings(), BrowserUrlFor(item.ServerKey)));
    }

    /// <summary>
    /// Lists a page of Metadata rows, narrowed by search, status, and source library.
    /// </summary>
    /// <param name="manager">Reads and writes the Metadata sync table.</param>
    /// <param name="search">Narrows the list to names or paths that match, when given.</param>
    /// <param name="status">Narrows the list to one status, when given.</param>
    /// <param name="sourceLibraryId">Narrows the list to one library on the source, when given.</param>
    /// <param name="skip">How many rows to pass over before the page starts.</param>
    /// <param name="take">The most rows the page holds, clamped to 200.</param>
    /// <returns>A page of rows with the total count, without the large JSON blobs.</returns>
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
    /// Counts the Metadata rows in each status.
    /// </summary>
    /// <param name="manager">Reads and writes the Metadata sync table.</param>
    /// <returns>The counts, with the last run failure when there is one.</returns>
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
    /// Queues the selected Metadata rows. With no IDs and a status, it queues every row in that status, which is how a retry reaches every errored row.
    /// </summary>
    /// <param name="manager">Reads and writes the Metadata sync table.</param>
    /// <param name="request">The IDs of the selected rows.</param>
    /// <returns>How many rows changed and which did not, or 400 when none was selected.</returns>
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
        // Any failure becomes a 500 the page can show.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue metadata sync items by IDs");
            return StatusCode(500, new { Error = "Could not queue the items. See the server log." });
        }
    }

    /// <summary>
    /// Marks the selected Metadata rows as Ignored.
    /// </summary>
    /// <param name="manager">Reads and writes the Metadata sync table.</param>
    /// <param name="request">The IDs of the selected rows.</param>
    /// <returns>How many rows changed and which did not, or 400 when none was selected.</returns>
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
        // Any failure becomes a 500 the page can show.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ignore metadata sync items by IDs");
            return StatusCode(500, new { Error = "Could not ignore the items. See the server log." });
        }
    }
}
