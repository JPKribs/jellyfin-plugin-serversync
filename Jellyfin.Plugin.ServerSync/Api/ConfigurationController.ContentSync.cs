using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Cleanup;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Downloads;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Api;

/// <summary>
/// The Content routes of the Sync tab and the Storage tab.
/// </summary>
public partial class ConfigurationController
{
    /// <summary>
    /// Lists a page of Content rows, narrowed by search, status, pending type, and Storage.
    /// </summary>
    /// <param name="manager">Reads and writes the Content sync table.</param>
    /// <param name="search">Narrows the list to paths that match, when given.</param>
    /// <param name="status">Narrows the list to one status, when given.</param>
    /// <param name="pendingType">Narrows the list to one pending type, when given.</param>
    /// <param name="cached">Whether to list only links whose file is in the link cache.</param>
    /// <param name="storage">The Storage to list, Copy or Link, or null for both.</param>
    /// <param name="serverKey">The key of the server entry whose rows are listed, or null for every server.</param>
    /// <param name="skip">How many rows to pass over before the page starts.</param>
    /// <param name="take">The most rows the page holds, clamped to 200.</param>
    /// <returns>A page of rows with the total count.</returns>
    [HttpGet("Items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PagedResult<SyncItemDto>> GetSyncItems(
        [FromServices] ContentSyncTableManager manager,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? pendingType = null,
        [FromQuery] bool cached = false,
        [FromQuery] string? storage = null,
        [FromQuery] string? serverKey = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50)
    {
        ArgumentNullException.ThrowIfNull(manager);
        var config = _configManager.Configuration;
        var cache = HttpContext?.RequestServices?.GetService(typeof(Services.Links.LinkCache)) as Services.Links.LinkCache;

        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);

        SyncStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<SyncStatus>(status, out var parsedStatus) && Enum.IsDefined(parsedStatus))
        {
            statusFilter = parsedStatus;
        }

        PendingType? pendingTypeFilter = null;
        if (!string.IsNullOrEmpty(pendingType) && Enum.TryParse<PendingType>(pendingType, out var parsedPendingType) && Enum.IsDefined(parsedPendingType))
        {
            pendingTypeFilter = parsedPendingType;
        }

        var cachedIds = cached ? cache?.List().Select(e => e.LinkId).ToList() ?? new List<string>() : null;
        bool? links = string.Equals(storage, "Link", StringComparison.OrdinalIgnoreCase) ? true
            : string.Equals(storage, "Copy", StringComparison.OrdinalIgnoreCase) ? false
            : null;
        
        // A key that names no server entry matches no row, so an unknown server lists nothing.
        var serverFilter = string.IsNullOrEmpty(serverKey) ? null : config.FindServer(serverKey)?.Key ?? serverKey;
        var (items, totalCount) = manager.SearchPaginated(search, statusFilter, pendingTypeFilter, skip, take, cachedIds, links, serverFilter);

        var libraryMappings = config.GetAllLibraryMappings();
        var libraryNameLookup = libraryMappings
            .GroupBy(m => m.SourceLibraryId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (SourceName: g.First().SourceLibraryName, LocalName: g.First().LocalLibraryName),
                StringComparer.OrdinalIgnoreCase);

        return Ok(new PagedResult<SyncItemDto>(
            items.Select(i =>
            {
                libraryNameLookup.TryGetValue(i.SourceLibraryId, out var libraryNames);
                var rowServer = config.ResolveServer(i.ServerKey);
                return new SyncItemDto
                {
                    Id = i.Id,
                    SourceItemId = i.SourceItemId,
                    SourceLibraryId = i.SourceLibraryId,
                    SourceLibraryName = libraryNames.SourceName,
                    LocalLibraryId = i.LocalLibraryId,
                    LocalLibraryName = libraryNames.LocalName,
                    SourcePath = i.SourcePath,
                    LocalPath = i.LocalPath,
                    SourceSize = i.SourceSize,
                    SourceSizeFormatted = FormatUtilities.FormatBytes(i.SourceSize),
                    SourceCreateDate = i.SourceCreateDate,
                    LocalItemId = i.LocalItemId,
                    Status = i.Status.ToString(),
                    PendingType = i.PendingType?.ToString(),
                    StatusDate = i.StatusDate,
                    LastSyncTime = i.LastSyncTime,
                    ErrorMessage = i.Reason,
                    RetryCount = i.RetryCount,
                    SourceServerUrl = rowServer?.BrowserUrl,
                    SourceServerId = rowServer?.ServerId,
                    ServerKey = i.ServerKey,
                    ServerName = rowServer?.DisplayName,
                    CompanionFiles = i.CompanionFiles,
                    StorageMode = i.StorageMode.ToString(),
                    Cached = i.StorageMode == Models.ContentSync.StorageMode.Link && cache?.Holds(i.SourceItemId) == true,
                    Kept = i.Kept == Models.ContentSync.KeptReason.None ? null : i.Kept.ToString()
                };
            }).ToList(),
            totalCount,
            skip,
            take));
    }

    /// <summary>
    /// Counts the Content rows in each status.
    /// </summary>
    /// <param name="manager">Reads and writes the Content sync table.</param>
    /// <returns>The counts, with the last run failure when there is one.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SyncStatusResponse> GetSyncStatus([FromServices] ContentSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        var counts = manager.GetStatusCounts();
        var pendingCounts = manager.GetPendingCounts();

        var response = new SyncStatusResponse
        {
            Pending = counts.GetValueOrDefault(SyncStatus.Pending, 0),
            PendingDownload = pendingCounts.GetValueOrDefault(PendingType.Download, 0),
            PendingReplacement = pendingCounts.GetValueOrDefault(PendingType.Replacement, 0),
            PendingDeletion = pendingCounts.GetValueOrDefault(PendingType.Deletion, 0),
            Queued = counts.GetValueOrDefault(SyncStatus.Queued, 0),
            Synced = counts.GetValueOrDefault(SyncStatus.Synced, 0),
            Errored = counts.GetValueOrDefault(SyncStatus.Errored, 0),
            Ignored = counts.GetValueOrDefault(SyncStatus.Ignored, 0),
            Deleting = counts.GetValueOrDefault(SyncStatus.Deleting, 0)
        };
        PopulateLastFailure(response, "Content");
        return Ok(response);
    }

    /// <summary>
    /// Sums the bytes waiting to sync, which is downloads plus replacements plus queued, less deletions.
    /// </summary>
    /// <param name="manager">Reads and writes the Content sync table.</param>
    /// <returns>The total with each part.</returns>
    [HttpGet("PendingSize")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PendingSizeResponse> GetPendingSize([FromServices] ContentSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        var sizes = manager.GetPendingSizes();

        return Ok(new PendingSizeResponse
        {
            PendingDownloadBytes = sizes.GetValueOrDefault("PendingDownload", 0),
            PendingReplacementBytes = sizes.GetValueOrDefault("PendingReplacement", 0),
            PendingDeletionBytes = sizes.GetValueOrDefault("PendingDeletion", 0),
            QueuedBytes = sizes.GetValueOrDefault("Queued", 0),
            TotalPendingBytes = sizes.GetValueOrDefault("Total", 0)
        });
    }

    /// <summary>
    /// Reads the disk space of each mapped library's folder.
    /// </summary>
    /// <returns>The disk space of each folder.</returns>
    [HttpGet("DiskSpace")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<DiskSpaceInfo>> GetDiskSpace()
    {
        return Ok(DiskSpaceService.GetDiskSpaceInfo(_configManager.Configuration));
    }

    /// <summary>
    /// Queues errored rows again, which gives each its full retry allowance back.
    /// </summary>
    /// <param name="manager">Reads and writes the Content sync table.</param>
    /// <param name="request">The rows to retry, or null for every errored row.</param>
    /// <returns>Whether the rows were queued.</returns>
    [HttpPost("RetryErroredItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult RetryErroredItems(
        [FromServices] ContentSyncTableManager manager,
        [FromBody] BulkItemsRequest? request = null)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (request?.SourceItemIds?.Count > 0)
        {
            foreach (var itemId in request.SourceItemIds)
            {
                manager.UpdateStatus(itemId, SyncStatus.Queued);
            }
        }
        else
        {
            var erroredItems = manager.GetByStatus(SyncStatus.Errored);
            foreach (var item in erroredItems)
            {
                manager.UpdateStatus(item.SourceItemId, SyncStatus.Queued);
            }
        }

        return Ok(new { Success = true });
    }

    /// <summary>
    /// Marks the selected rows as Ignored.
    /// </summary>
    /// <param name="manager">Reads and writes the Content sync table.</param>
    /// <param name="request">The source item IDs of the selected rows.</param>
    /// <returns>How many rows changed, or 400 when none was selected.</returns>
    [HttpPost("IgnoreItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult IgnoreItems(
        [FromServices] ContentSyncTableManager manager,
        [FromBody] BulkItemsRequest request)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (request?.SourceItemIds == null || request.SourceItemIds.Count == 0)
        {
            return BadRequest("Select the items.");
        }

        try
        {
            var updated = manager.BatchUpdateStatus(request.SourceItemIds, SyncStatus.Ignored);
            return Ok(new { Updated = updated });
        }
        // Any failure becomes a 500 the page can show.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ignore {Count} content item(s)", request.SourceItemIds.Count);
            return StatusCode(500, new { Error = "Could not ignore the items. See the server log." });
        }
    }

    /// <summary>
    /// Queues the selected rows, whatever status they are in.
    /// </summary>
    /// <param name="manager">Reads and writes the Content sync table.</param>
    /// <param name="request">The source item IDs of the selected rows.</param>
    /// <returns>How many rows changed, or 400 when none was selected.</returns>
    [HttpPost("QueueItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult QueueItems(
        [FromServices] ContentSyncTableManager manager,
        [FromBody] BulkItemsRequest request)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (request?.SourceItemIds == null || request.SourceItemIds.Count == 0)
        {
            return BadRequest("Select the items.");
        }

        try
        {
            var updated = manager.BatchUpdateStatus(request.SourceItemIds, SyncStatus.Queued);
            return Ok(new { Updated = updated });
        }
        // Any failure becomes a 500 the page can show.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue {Count} content item(s)", request.SourceItemIds.Count);
            return StatusCode(500, new { Error = "Could not queue the items. See the server log." });
        }
    }

    /// <summary>Reports what the link cache holds, with its file count, disk use, and folder.</summary>
    /// <param name="cache">Reads and empties the link cache.</param>
    /// <param name="filler">Reports how many files wait to be cached.</param>
    /// <returns>The cache's state.</returns>
    [HttpGet("LinkCache")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetLinkCache([FromServices] Services.Links.LinkCache cache, [FromServices] Services.Links.LinkCacheFiller filler)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(filler);
        var used = cache.Enabled ? cache.UsedBytes() : 0;
        var limit = cache.LimitBytes();
        return Ok(new
        {
            cache.Enabled,
            Sizing = _configManager.Configuration.EffectiveLinkCacheSizing.ToString(),
            Location = cache.Root,
            Files = cache.Enabled ? cache.List().Count : 0,
            UsedBytes = used,
            UsedFormatted = FormatUtilities.FormatBytes(used),
            LimitBytes = limit,
            LimitFormatted = FormatUtilities.FormatBytes(limit),
            filler.Waiting
        });
    }

    /// <summary>Empties the link cache. Links keep playing from their sources.</summary>
    /// <param name="cache">Reads and empties the link cache.</param>
    /// <returns>204 once the cache is empty.</returns>
    [HttpPost("LinkCache/Clear")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ClearLinkCache([FromServices] Services.Links.LinkCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        cache.Wipe();
        _logger.LogInformation("The link cache was emptied from the dashboard");
        return NoContent();
    }

    /// <summary>
    /// Turns links into permanent copies, skipping rows that are not links.
    /// A link whose whole file is cached is moved into place by a run that starts at once, and any other downloads on the next run.
    /// </summary>
    /// <param name="manager">Reads and writes the Content sync table.</param>
    /// <param name="cache">Reads and empties the link cache.</param>
    /// <param name="request">The source item IDs of the selected rows.</param>
    /// <returns>How many links will be copied, how many of those come from the cache, and how many rows were skipped.</returns>
    [HttpPost("PromoteItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult PromoteItems(
        [FromServices] ContentSyncTableManager manager,
        [FromServices] Services.Links.LinkCache cache,
        [FromBody] BulkItemsRequest request)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(cache);
        if (request?.SourceItemIds == null || request.SourceItemIds.Count == 0)
        {
            return BadRequest("Select the items.");
        }

        var promoted = 0;
        var fromCache = 0;
        var skipped = 0;
        foreach (var id in request.SourceItemIds)
        {
            var row = manager.GetBySourceItemId(id);
            if (row is not null
                && row.Status is not (SyncStatus.Ignored or SyncStatus.Deleting)
                && !(row.Status == SyncStatus.Pending && row.PendingType == PendingType.Deletion)
                && Services.Links.LinkStateService.MarkPromoting(row, requireApproval: false))
            {
                // Kept by hand, so removing an Always Keep pick never makes it a link.
                row.Kept = Models.ContentSync.KeptReason.Hand;
                manager.Upsert(row);
                promoted++;
                if (cache.HoldsWhole(id, row.SourceSize))
                {
                    fromCache++;
                }
            }
            else
            {
                skipped++;
            }
        }

        // A cached file moves in seconds, so a run is queued at once.
        if (fromCache > 0 && _taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == "ServerSyncDownloadContent") is { } sync)
        {
            _taskManager.QueueScheduledTask(sync.ScheduledTask, new TaskOptions());
        }

        return Ok(new { Promoted = promoted, FromCache = fromCache, Skipped = skipped });
    }

    /// <summary>
    /// Lists the permanent copies one server's Link libraries hold, for the Storage tab, with why each is a copy.
    /// Copies already going back to links are listed too.
    /// </summary>
    /// <param name="manager">Reads and writes the Content sync table.</param>
    /// <param name="serverKey">The key of the server entry whose copies are listed.</param>
    /// <param name="search">Narrows the list to paths that match, when given.</param>
    /// <param name="skip">How many rows to pass over before the page starts.</param>
    /// <param name="take">The most rows the page holds, clamped to 200.</param>
    /// <returns>A page of rows with the total count.</returns>
    [HttpGet("KeptCopies")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PagedResult<SyncItemDto>> GetKeptCopies(
        [FromServices] ContentSyncTableManager manager,
        [FromQuery] string? serverKey = null,
        [FromQuery] string? search = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50)
    {
        ArgumentNullException.ThrowIfNull(manager);
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);
        var server = string.IsNullOrEmpty(serverKey) ? null : _configManager.Configuration.FindServer(serverKey);
        var libraries = server?.LibraryMappings?
            .Where(m => m.StorageMode == Models.ContentSync.StorageMode.Link && !string.IsNullOrEmpty(m.SourceLibraryId))
            .Select(m => m.SourceLibraryId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        var (items, total) = server is null ? (Array.Empty<SyncItem>(), 0) : manager.SearchKeptCopies(server.Key, libraries, search, skip, take);
        var names = (server?.LibraryMappings ?? new List<Models.Configuration.LibraryMapping>())
            .GroupBy(m => m.SourceLibraryId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return Ok(new PagedResult<SyncItemDto>(
            items.Select(i =>
            {
                names.TryGetValue(i.SourceLibraryId, out var mapping);
                return new SyncItemDto
                {
                    Id = i.Id,
                    SourceItemId = i.SourceItemId,
                    SourceLibraryId = i.SourceLibraryId,
                    SourceLibraryName = mapping?.SourceLibraryName,
                    LocalLibraryId = i.LocalLibraryId,
                    LocalLibraryName = mapping?.LocalLibraryName,
                    SourcePath = i.SourcePath,
                    LocalPath = i.LocalPath,
                    SourceSize = i.SourceSize,
                    SourceSizeFormatted = FormatUtilities.FormatBytes(i.SourceSize),
                    SourceCreateDate = i.SourceCreateDate,
                    LocalItemId = i.LocalItemId,
                    Status = i.Status.ToString(),
                    StatusDate = i.StatusDate,
                    LastSyncTime = i.LastSyncTime,
                    ErrorMessage = i.Reason,
                    ServerKey = i.ServerKey,
                    ServerName = server?.DisplayName,
                    StorageMode = i.StorageMode.ToString(),
                    Kept = i.Kept == Models.ContentSync.KeptReason.None ? "Unknown" : i.Kept.ToString()
                };
            }).ToList(),
            total,
            skip,
            take));
    }

    /// <summary>
    /// Queues permanent copies in Link libraries to Turn Back Into Links, which the next run does after checking the source still has each file.
    /// A copy an Always Keep pick still covers is skipped, since the next scan would only copy it again.
    /// </summary>
    /// <param name="manager">Reads and writes the Content sync table.</param>
    /// <param name="request">The IDs of the selected rows.</param>
    /// <returns>How many were queued, and why any were skipped.</returns>
    [HttpPost("KeptCopies/Unkeep")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult UnkeepCopies([FromServices] ContentSyncTableManager manager, [FromBody] Models.ContentSync.UnkeepRequest request)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (request?.Ids is not { Count: > 0 })
        {
            return BadRequest("Select the items.");
        }

        var config = _configManager.Configuration;
        var queued = 0;
        var skipped = new List<object>();
        foreach (var id in request.Ids.Distinct())
        {
            var row = manager.GetById(id);
            var mapping = row is null ? null : config.FindServer(row.ServerKey ?? string.Empty)?.LibraryMappings?
                .Where(m => string.Equals(m.SourceLibraryId, row.SourceLibraryId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => UnderRoot(row.SourcePath, m.SourceRootPath))
                .FirstOrDefault();
            string? reason = row is null ? "the row no longer exists"
                : row.StorageMode != Models.ContentSync.StorageMode.Copy ? "it is not a copy"
                : row.Status != SyncStatus.Synced ? "it is not a synced copy yet"
                : mapping is null || mapping.StorageMode != Models.ContentSync.StorageMode.Link ? "its library is not mapped as Link"
                : Services.Links.AlwaysKeep.Covers(mapping, row.SourcePath) ? "an Always Keep pick covers it. Remove the pick on the Storage tab instead"
                : Services.Links.LinkPaths.LinkPathFor(row.LocalPath ?? string.Empty) is null ? "it cannot be a link"
                : !System.IO.File.Exists(row.LocalPath) ? "its file is not here"
                : null;
            if (reason is not null)
            {
                skipped.Add(new { Id = id, Reason = reason });
                continue;
            }

            row!.StorageMode = Models.ContentSync.StorageMode.Unkeeping;
            row.Status = SyncStatus.Queued;
            row.PendingType = null;
            row.StatusDate = DateTime.UtcNow;
            row.RetryCount = 0;
            row.Reason = null;
            manager.Upsert(row);
            queued++;
        }

        if (queued > 0 && _taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == "ServerSyncDownloadContent") is { } sync)
        {
            _taskManager.QueueScheduledTask(sync.ScheduledTask, new TaskOptions());
        }

        return Ok(new { Queued = queued, Skipped = skipped });
    }

    /// <summary>Whether a path lies under a root folder, on either kind of separator.</summary>
    private static bool UnderRoot(string? path, string? root)
        => !string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(root)
            && path.Replace('\\', '/').StartsWith(root.Replace('\\', '/').TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);


    /// <summary>
    /// Marks rows as Synced after checking each local file exists, to settle rows stuck in Queued or Errored.
    /// </summary>
    /// <param name="manager">Reads and writes the Content sync table.</param>
    /// <param name="request">The source item IDs of the selected rows.</param>
    /// <returns>How many rows were marked, and how many had no file.</returns>
    [HttpPost("MarkSynced")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult MarkSynced(
        [FromServices] ContentSyncTableManager manager,
        [FromBody] BulkItemsRequest request)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (request?.SourceItemIds == null || request.SourceItemIds.Count == 0)
        {
            return BadRequest("Select the items.");
        }

        var syncedCount = 0;
        var notFoundCount = 0;

        foreach (var itemId in request.SourceItemIds)
        {
            try
            {
                var item = manager.GetBySourceItemId(itemId);
                if (item == null)
                {
                    notFoundCount++;
                    continue;
                }

                if (!string.IsNullOrEmpty(item.LocalPath) && System.IO.File.Exists(item.LocalPath))
                {
                    manager.UpdateStatus(itemId, SyncStatus.Synced);
                    syncedCount++;
                    _logger.LogInformation(
                        "Manually marked {FileName} as synced (local file verified at {LocalPath})",
                        SanitizeForLog(Path.GetFileName(item.LocalPath)),
                        SanitizeForLog(item.LocalPath));
                }
                else
                {
                    notFoundCount++;
                    _logger.LogWarning(
                        "Cannot mark {ItemId} as synced: local file not found at {LocalPath}",
                        SanitizeForLog(itemId),
                        SanitizeForLog(item.LocalPath));
                }
            }
            // One row's failure must not stop the rest.
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to mark item {ItemId} as synced", SanitizeForLog(itemId));
                notFoundCount++;
            }
        }

        return Ok(new { Synced = syncedCount, NotFound = notFoundCount });
    }

    /// <summary>
    /// Deletes the selected rows' files from this server only, and keeps each handled row as Ignored.
    /// </summary>
    /// <param name="manager">Reads and writes the Content sync table.</param>
    /// <param name="request">The source item IDs of the selected rows.</param>
    /// <returns>How many files were deleted, failed, and skipped.</returns>
    [HttpPost("DeleteLocalItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult DeleteLocalItems(
        [FromServices] ContentSyncTableManager manager,
        [FromBody] BulkItemsRequest request)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (request?.SourceItemIds == null || request.SourceItemIds.Count == 0)
        {
            return BadRequest("Select the items.");
        }

        var config = _configManager.Configuration;
        var deletedCount = 0;
        var failedCount = 0;
        var skippedCount = 0;

        _logger.LogInformation("Starting deletion of {Count} items from local server", request.SourceItemIds.Count);

        foreach (var sourceItemId in request.SourceItemIds)
        {
            var item = manager.GetBySourceItemId(sourceItemId);
            if (item == null)
            {
                _logger.LogWarning("SKIPPED DELETE: Item {SourceItemId} not found in tracking database", SanitizeForLog(sourceItemId));
                skippedCount++;
                continue;
            }

            var fileName = string.IsNullOrEmpty(item.LocalPath) ? null : Path.GetFileName(item.LocalPath);
            var sanitizedFileName = SanitizeForLog(fileName);
            var sanitizedLocalPath = SanitizeForLog(item.LocalPath);

            if (!string.IsNullOrEmpty(item.LocalPath) && !FileValidationService.IsPathWithinLibrary(item.LocalPath, config))
            {
                _logger.LogWarning(
                    "SKIPPED DELETE: {FileName} - Path is outside configured library directories. Local path: {LocalPath}",
                    sanitizedFileName,
                    sanitizedLocalPath);
                skippedCount++;
                continue;
            }

            if (!string.IsNullOrEmpty(item.LocalPath))
            {
                var containingRoot = FileValidationService.GetContainingLibraryRoot(item.LocalPath, config);
                if (containingRoot != null && FileValidationService.HasSymlinkedDirectoryComponent(item.LocalPath, containingRoot))
                {
                    _logger.LogWarning(
                        "SKIPPED DELETE: {FileName} - Path traverses a symlinked directory. The physical file may live outside the library. Local path: {LocalPath}",
                        sanitizedFileName,
                        sanitizedLocalPath);
                    skippedCount++;
                    continue;
                }
            }

            MediaBrowser.Controller.Entities.BaseItem? localItem = null;

            if (!string.IsNullOrEmpty(item.LocalItemId) && Guid.TryParse(item.LocalItemId, out var localItemGuid))
            {
                localItem = _libraryManager.GetItemById(localItemGuid);
            }

            if (localItem == null && !string.IsNullOrEmpty(item.LocalPath))
            {
                localItem = _libraryManager.FindByPath(item.LocalPath, isFolder: false);
            }

            // A stale LocalItemId may point elsewhere, so the resolved path must match.
            if (localItem != null)
            {
                var resolvedPath = localItem.Path;
                var mismatch = !string.IsNullOrEmpty(item.LocalPath)
                    && !string.Equals(resolvedPath, item.LocalPath, StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrEmpty(resolvedPath)
                    || mismatch
                    || !FileValidationService.IsPathWithinLibrary(resolvedPath, config))
                {
                    _logger.LogWarning(
                        "Stale LocalItemId for {FileName}: resolved item path {ResolvedPath} does not match the tracked path or escapes library roots. Falling back to the tracked path",
                        sanitizedFileName,
                        SanitizeForLog(resolvedPath));
                    localItem = null;
                }
            }

            var fileExists = localItem != null || (!string.IsNullOrEmpty(item.LocalPath) && System.IO.File.Exists(item.LocalPath));

            if (!fileExists)
            {
                _logger.LogWarning(
                    "SKIPPED DELETE: {FileName} - File not found in Jellyfin library or on disk, so the row is kept as Ignored. Source path: {SourcePath}",
                    sanitizedFileName,
                    SanitizeForLog(item.SourcePath));
                KeepAsIgnored(manager, item);
                skippedCount++;
                continue;
            }

            var targetPath = !string.IsNullOrEmpty(item.LocalPath) ? item.LocalPath : localItem?.Path;
            // An item path the row does not track still needs the symlink check.
            if (string.IsNullOrEmpty(item.LocalPath) && !string.IsNullOrEmpty(targetPath))
            {
                var targetRoot = FileValidationService.GetContainingLibraryRoot(targetPath, config);
                if (targetRoot != null && FileValidationService.HasSymlinkedDirectoryComponent(targetPath, targetRoot))
                {
                    _logger.LogWarning(
                        "SKIPPED DELETE: {FileName} - Path traverses a symlinked directory. The physical file may live outside the library. Item path: {ItemPath}",
                        sanitizedFileName,
                        SanitizeForLog(targetPath));
                    skippedCount++;
                    continue;
                }
            }

            var result = FileDeletionService.DeleteLocalFile(item.LocalPath, localItem, _libraryManager, config, _logger);

            if (result.Success && result.AlreadyGone)
            {
                _logger.LogInformation(
                    "DELETE (external): {FileName} - The file was already gone, so only the stale library entry was removed and the row is kept as Ignored. Local path: {LocalPath}",
                    sanitizedFileName,
                    SanitizeForLog(targetPath));
                KeepAsIgnored(manager, item);
                deletedCount++;
            }
            else if (result.Success)
            {
                var useRecyclingBin = config.EnableRecyclingBin && !string.IsNullOrEmpty(config.RecyclingBinPath);
                var action = useRecyclingBin ? "RECYCLED" : "DELETED";
                var suffix = localItem == null ? " (direct)" : string.Empty;
                _logger.LogInformation("{Action}{Suffix}: {FileName} - Local path: {LocalPath}", action, suffix, sanitizedFileName, sanitizedLocalPath);
                KeepAsIgnored(manager, item);
                deletedCount++;
            }
            else
            {
                // A folder counts too, so a refused disc structure is reported as failed.
                var stillExists = !string.IsNullOrEmpty(targetPath)
                    && (System.IO.File.Exists(targetPath) || System.IO.Directory.Exists(targetPath));
                if (!stillExists)
                {
                    _logger.LogInformation(
                        "DELETE (external): {FileName} - File no longer exists after deletion attempt, so the row is kept as Ignored. Local path: {LocalPath}",
                        sanitizedFileName,
                        sanitizedLocalPath);
                    KeepAsIgnored(manager, item);
                    deletedCount++;
                }
                else
                {
                    _logger.LogError("FAILED DELETE: {FileName} - {Error}. Local path: {LocalPath}", sanitizedFileName, SanitizeForLog(result.ErrorMessage), sanitizedLocalPath);
                    failedCount++;
                }
            }
        }

        _logger.LogInformation(
            "Deletion task complete: {Deleted} deleted, {Failed} failed, {Skipped} skipped out of {Total} items",
            deletedCount,
            failedCount,
            skippedCount,
            request.SourceItemIds.Count);

        return Ok(new { Deleted = deletedCount, Failed = failedCount, Skipped = skippedCount });
    }

    /// <summary>Keeps a row whose file is gone as Ignored, so the next scan does not download it again.</summary>
    private static void KeepAsIgnored(ContentSyncTableManager manager, SyncItem item)
    {
        item.Status = SyncStatus.Ignored;
        item.PendingType = null;
        item.LocalItemId = null;
        item.StatusDate = DateTime.UtcNow;
        manager.Upsert(item);
    }
}
