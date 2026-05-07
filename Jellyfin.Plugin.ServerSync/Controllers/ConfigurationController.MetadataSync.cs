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
    /// GetMetadataSyncItem
    /// Gets a specific metadata sync item by its ID.
    /// </summary>
    /// <param name="id">The database ID of the metadata sync item.</param>
    /// <returns>The metadata sync item DTO.</returns>
    [HttpGet("MetadataItems/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<MetadataSyncItemDto> GetMetadataSyncItem(
        long id,
        [FromServices] MetadataSyncTableManager manager,
        [FromServices] MetadataSyncTableService metadataService)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(metadataService);
        var item = manager.GetById(id);
        if (item == null)
        {
            return NotFound();
        }

        var config = _configManager.Configuration;

        // Re-read the live local item before returning. Without this, the
        // modal shows the local snapshot from the last metadata Refresh —
        // so a successful Sync apply that just wrote new data to the local
        // server doesn't show up here until the user re-runs Refresh,
        // making it look like sync silently failed.
        metadataService.RefreshLocalSnapshot(
            item,
            syncMetadata: config.MetadataSyncMetadata,
            syncImages: config.MetadataSyncImages,
            syncPeople: config.MetadataSyncPeople,
            syncStudios: config.MetadataSyncStudios,
            syncGenres: config.MetadataSyncGenres,
            syncTags: config.MetadataSyncTags);

        return Ok(MapToMetadataSyncItemDto(item, config.LibraryMappings, !string.IsNullOrEmpty(config.SourceServerExternalUrl) ? config.SourceServerExternalUrl : config.SourceServerUrl, config.SourceServerApiKey));
    }

    /// <summary>
    /// GetMetadataItemImageInfo
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
    /// GetMetadataSyncItems
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
    public ActionResult<PaginatedResult<MetadataSyncItemDto>> GetMetadataSyncItems(
        [FromServices] MetadataSyncTableManager manager,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? sourceLibraryId = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50)
    {
        ArgumentNullException.ThrowIfNull(manager);

        // Clamp pagination values
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);

        // Parse status filter
        SyncStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<SyncStatus>(status, out var parsedStatus))
        {
            statusFilter = parsedStatus;
        }

        // Get paginated results
        var (items, totalCount) = manager.SearchMetadataSyncItemsPaginated(
            search, statusFilter, sourceLibraryId, skip, take);

        var config = _configManager.Configuration;

        return Ok(new PaginatedResult<MetadataSyncItemDto>
        {
            Items = items.Select(i => MapToMetadataSyncItemDto(
                i,
                config.LibraryMappings,
                !string.IsNullOrEmpty(config.SourceServerExternalUrl) ? config.SourceServerExternalUrl : config.SourceServerUrl,
                config.SourceServerApiKey,
                includeBlobs: false)).ToList(),
            TotalCount = totalCount,
            Skip = skip,
            Take = take
        });
    }

    /// <summary>
    /// GetMetadataSyncStatus
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

        return Ok(new MetadataSyncStatusResponse
        {
            Pending = counts.GetValueOrDefault(SyncStatus.Pending, 0),
            Queued = counts.GetValueOrDefault(SyncStatus.Queued, 0),
            Synced = counts.GetValueOrDefault(SyncStatus.Synced, 0),
            Errored = counts.GetValueOrDefault(SyncStatus.Errored, 0),
            Ignored = counts.GetValueOrDefault(SyncStatus.Ignored, 0),
            LastSyncTime = _configManager.Configuration.LastMetadataSyncTime,
            LibraryCount = libraryMappings.Count
        });
    }

    /// <summary>
    /// UpdateMetadataSyncItemStatus
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
        if (!Enum.TryParse<SyncStatus>(request.Status, out var status))
        {
            return BadRequest("Invalid status value");
        }

        manager.UpdateStatus(request.Id, status);
        return Ok(new { Success = true });
    }

    /// <summary>
    /// QueueMetadataSyncItems
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

        var successCount = 0;

        try
        {
            successCount = manager.BatchUpdateStatusByIds(request.Ids, SyncStatus.Queued);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to queue metadata sync items by IDs");
        }

        return Ok(new { Updated = successCount });
    }

    /// <summary>
    /// IgnoreMetadataSyncItems
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

        var successCount = 0;

        try
        {
            successCount = manager.BatchUpdateStatusByIds(request.Ids, SyncStatus.Ignored);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ignore metadata sync items by IDs");
        }

        return Ok(new { Updated = successCount });
    }

    /// <summary>
    /// TriggerMetadataRefresh
    /// Manually triggers the refresh metadata sync table task.
    /// </summary>
    /// <returns>Action result with status message.</returns>
    [HttpPost("TriggerMetadataRefresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerMetadataRefresh()
        => ExecuteScheduledTaskByKey("ServerSyncRefreshMetadataTable", "Metadata refresh task started", "Metadata refresh task not found");

    /// <summary>
    /// TriggerMetadataSync
    /// Manually triggers the sync missing metadata task.
    /// </summary>
    /// <returns>Action result with status message.</returns>
    [HttpPost("TriggerMetadataSync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerMetadataSync()
        => ExecuteScheduledTaskByKey("ServerSyncMissingMetadata", "Metadata sync task started", "Metadata sync task not found");

    /// <summary>
    /// ResetMetadataSyncDatabase
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

    /// <summary>
    /// Maps a MetadataSyncItem to a DTO.
    /// </summary>
    private static MetadataSyncItemDto MapToMetadataSyncItemDto(MetadataSyncItem item, List<LibraryMapping>? libraryMappings, string? sourceServerUrl, string? sourceServerApiKey)
        => MapToMetadataSyncItemDto(item, libraryMappings, sourceServerUrl, sourceServerApiKey, includeBlobs: true);

    /// <summary>
    /// Maps a <see cref="MetadataSyncItem"/> to its DTO. When
    /// <paramref name="includeBlobs"/> is <c>false</c>, the eight large
    /// JSON-blob fields are left null and per-category change flags are
    /// derived from hash mismatch alone — used by the list endpoint to keep
    /// the response compact (a 50-row page would otherwise ship ~megabytes
    /// of JSON the UI doesn't render).
    /// </summary>
    private static MetadataSyncItemDto MapToMetadataSyncItemDto(
        MetadataSyncItem item,
        List<LibraryMapping>? libraryMappings,
        string? sourceServerUrl,
        string? sourceServerApiKey,
        bool includeBlobs)
    {
        // Look up library names
        string? sourceLibraryName = null;
        string? localLibraryName = null;

        var mapping = libraryMappings?.FirstOrDefault(m => m.SourceLibraryId == item.SourceLibraryId);
        if (mapping != null)
        {
            sourceLibraryName = mapping.SourceLibraryName;
            localLibraryName = mapping.LocalLibraryName;
        }

        bool hasMetadataChanges;
        bool hasImagesChanges;
        bool hasPeopleChanges;
        bool hasStudiosChanges;
        string? changesSummary;

        if (includeBlobs)
        {
            hasMetadataChanges = item.HasMetadataChanges;
            hasImagesChanges = item.HasImagesChanges;
            hasPeopleChanges = item.HasPeopleChanges;
            hasStudiosChanges = item.HasStudiosChanges;
            changesSummary = item.ChangesSummary;
        }
        else
        {
            // Hash-mismatch is a fast, blob-free proxy for "has changes".
            // Used by the list view; the per-item detail endpoint still
            // returns the full deep-compare answer.
            static bool HashMismatch(string? src, string? synced)
                => !string.IsNullOrEmpty(src)
                    && !string.Equals(src, synced, StringComparison.Ordinal);

            hasMetadataChanges = HashMismatch(item.Metadata.SourceHash, item.Metadata.SyncedHash);
            hasImagesChanges = HashMismatch(item.Images.SourceHash, item.Images.SyncedHash);
            hasPeopleChanges = HashMismatch(item.People.SourceHash, item.People.SyncedHash);
            hasStudiosChanges = HashMismatch(item.Studios.SourceHash, item.Studios.SyncedHash);

            var changedCount = (hasMetadataChanges ? 1 : 0)
                + (hasImagesChanges ? 1 : 0)
                + (hasPeopleChanges ? 1 : 0)
                + (hasStudiosChanges ? 1 : 0);
            changesSummary = changedCount switch
            {
                0 => "No changes",
                1 => "1 category",
                _ => $"{changedCount} categories"
            };
        }

        return new MetadataSyncItemDto
        {
            Id = item.Id,
            SourceLibraryId = item.SourceLibraryId,
            LocalLibraryId = item.LocalLibraryId,
            SourceLibraryName = sourceLibraryName,
            LocalLibraryName = localLibraryName,
            SourceItemId = item.SourceItemId,
            LocalItemId = item.LocalItemId,
            ItemName = item.ItemName,
            SourcePath = item.SourcePath,
            LocalPath = item.LocalPath,
            SourceMetadataValue = includeBlobs ? item.Metadata.Source : null,
            LocalMetadataValue = includeBlobs ? item.Metadata.Local : null,
            SourceImagesValue = includeBlobs ? item.Images.Source : null,
            LocalImagesValue = includeBlobs ? item.Images.Local : null,
            SourcePeopleValue = includeBlobs ? item.People.Source : null,
            LocalPeopleValue = includeBlobs ? item.People.Local : null,
            SourceStudiosValue = includeBlobs ? item.Studios.Source : null,
            LocalStudiosValue = includeBlobs ? item.Studios.Local : null,
            HasMetadataChanges = hasMetadataChanges,
            HasImagesChanges = hasImagesChanges,
            HasPeopleChanges = hasPeopleChanges,
            HasStudiosChanges = hasStudiosChanges,
            HasChanges = hasMetadataChanges || hasImagesChanges || hasPeopleChanges || hasStudiosChanges,
            ChangesSummary = changesSummary,
            SourceServerUrl = sourceServerUrl,
            SourceServerApiKey = sourceServerApiKey,
            Status = item.Status.ToString(),
            StatusDate = item.StatusDate,
            LastSyncTime = item.LastSyncTime,
            ErrorMessage = item.Reason
        };
    }
}
