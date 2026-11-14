using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// Content sync endpoints for Server Sync plugin.
/// </summary>
public partial class ConfigurationController
{
    /// <summary>
    /// Gets paginated sync items from the database with optional search and filter.
    /// </summary>
    /// <param name="search">Optional search term.</param>
    /// <param name="status">Optional status filter.</param>
    /// <param name="pendingType">Optional pending type filter.</param>
    /// <param name="cached">Only links whose file is in the link cache.</param>
    /// <param name="skip">Number of items to skip (default 0).</param>
    /// <param name="take">Maximum items to return (default 50, max 200).</param>
    /// <returns>Paginated result of sync item DTOs.</returns>
    [HttpGet("Items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PagedResult<SyncItemDto>> GetSyncItems(
        [FromServices] ContentSyncTableManager manager,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? pendingType = null,
        [FromQuery] bool cached = false,
        [FromQuery] string? storage = null,
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
        var (items, totalCount) = manager.SearchPaginated(search, statusFilter, pendingTypeFilter, skip, take, cachedIds, links);

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
    /// Gets sync status counts.
    /// </summary>
    /// <returns>Sync status response with counts.</returns>
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
    /// Gets detailed sync statistics for health dashboard.
    /// </summary>
    /// <returns>Sync stats response.</returns>
    [HttpGet("Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<SyncStatsResponse> GetSyncStats([FromServices] ContentSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        try
        {
            var config = _configManager.Configuration;
            var stats = manager.GetSyncStats();
            var pendingCounts = manager.GetPendingCounts();
            var diskInfo = DiskSpaceService.GetMinimumDiskSpaceInfo(config);

            return Ok(new SyncStatsResponse
            {
                TotalItems = stats.StatusCounts.Values.Sum(),
                SyncedItems = stats.StatusCounts.GetValueOrDefault(SyncStatus.Synced, 0),
                QueuedItems = stats.StatusCounts.GetValueOrDefault(SyncStatus.Queued, 0),
                ErroredItems = stats.StatusCounts.GetValueOrDefault(SyncStatus.Errored, 0),
                PendingItems = stats.StatusCounts.GetValueOrDefault(SyncStatus.Pending, 0),
                PendingDownloadItems = pendingCounts.GetValueOrDefault(PendingType.Download, 0),
                PendingReplacementItems = pendingCounts.GetValueOrDefault(PendingType.Replacement, 0),
                PendingDeletionItems = pendingCounts.GetValueOrDefault(PendingType.Deletion, 0),
                IgnoredItems = stats.StatusCounts.GetValueOrDefault(SyncStatus.Ignored, 0),
                TotalSyncedBytes = stats.TotalSyncedBytes,
                TotalQueuedBytes = stats.TotalQueuedBytes,
                LastSyncTime = stats.LastSyncTime,
                LastSyncStartTime = config.LastSyncStartTime,
                LastSyncEndTime = config.LastSyncEndTime,
                FreeDiskSpaceBytes = diskInfo?.FreeBytes ?? 0,
                MinimumRequiredBytes = (long)config.MinimumFreeDiskSpaceGb * 1024 * 1024 * 1024,
                HasSufficientDiskSpace = diskInfo?.IsSufficient ?? true
            });
        }
        catch (ObjectDisposedException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Database is not available");
        }
    }

    /// <summary>
    /// Gets the total size of pending items to be synced.
    /// Calculates: PendingDownload + PendingReplacement + Queued - PendingDeletion
    /// </summary>
    /// <returns>Pending size response with breakdown.</returns>
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
    /// Gets disk space information for configured library paths.
    /// </summary>
    /// <returns>List of disk space info.</returns>
    [HttpGet("DiskSpace")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<DiskSpaceInfo>> GetDiskSpace()
    {
        return Ok(DiskSpaceService.GetDiskSpaceInfo(_configManager.Configuration));
    }

    /// <summary>
    /// Manually triggers the sync task.
    /// </summary>
    /// <returns>Action result with status message.</returns>
    [HttpPost("TriggerSync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerSync()
        => ExecuteScheduledTaskByKey("ServerSyncDownloadContent", "Sync task started", "Download task not found");

    /// <summary>
    /// Manually triggers the refresh sync table task.
    /// </summary>
    /// <returns>Action result with status message.</returns>
    [HttpPost("TriggerRefresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerRefresh()
        => ExecuteScheduledTaskByKey("ServerSyncUpdateTables", "Refresh task started", "Refresh task not found");

    /// <summary>
    /// Resets errored items for retry.
    /// </summary>
    /// <param name="request">Optional request with specific item IDs.</param>
    /// <returns>Action result with success status.</returns>
    [HttpPost("RetryErroredItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult RetryErroredItems(
        [FromServices] ContentSyncTableManager manager,
        [FromBody] BulkItemsRequest? request = null)
    {
        ArgumentNullException.ThrowIfNull(manager);
        // Queued via UpdateStatus is an operator action, so the transition
        // clauses reset RetryCount, the row gets its full allowance back.
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
    /// Updates the status of a sync item.
    /// </summary>
    /// <param name="request">Status update request.</param>
    /// <returns>Action result with success status.</returns>
    [HttpPost("UpdateItemStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult UpdateItemStatus(
        [FromServices] ContentSyncTableManager manager,
        [FromBody] UpdateItemStatusRequest request)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.TryParse<SyncStatus>(request.Status, out var status) || !Enum.IsDefined(status)
            || status == SyncStatus.Deleting)
        {
            // Deleting schedules unattended file removal. It is only ever set
            // by the refresh pipeline or the approval flow, never directly.
            return BadRequest("Invalid status value");
        }

        manager.UpdateStatus(request.SourceItemId, status);
        return Ok(new { Success = true });
    }

    /// <summary>
    /// Marks multiple items as ignored.
    /// </summary>
    /// <param name="request">Bulk items request.</param>
    /// <returns>Action result with updated count.</returns>
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
            return BadRequest("No items specified");
        }

        // Count the rows the update actually touched. Counting every posted id reported ids with no
        // row behind them as updated.
        try
        {
            var updated = manager.BatchUpdateStatus(request.SourceItemIds, SyncStatus.Ignored);
            return Ok(new { Updated = updated });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ignore {Count} content item(s)", request.SourceItemIds.Count);
            return StatusCode(500, new { Error = "Bulk ignore failed. See server log" });
        }
    }

    /// <summary>
    /// Moves items to Queued status (works for Pending, Ignored, Errored, and Synced items).
    /// </summary>
    /// <param name="request">Bulk items request.</param>
    /// <returns>Action result with updated count.</returns>
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
            return BadRequest("No items specified");
        }

        // Count the rows the update actually touched. Counting every posted id reported ids with no
        // row behind them as updated.
        try
        {
            var updated = manager.BatchUpdateStatus(request.SourceItemIds, SyncStatus.Queued);
            return Ok(new { Updated = updated });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue {Count} content item(s)", request.SourceItemIds.Count);
            return StatusCode(500, new { Error = "Bulk queue failed. See server log" });
        }
    }

    /// <summary>What the link cache holds now: how many files, how much disk, and where.</summary>
    /// <param name="cache">The link cache.</param>
    /// <param name="filler">Fills the cache.</param>
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
    /// <param name="cache">The link cache.</param>
    /// <returns>No content.</returns>
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
    /// Turns links into full copies, each link's .strm removed once its copy is in place. A link whose
    /// whole file is in the link cache has the cached file moved into place, by a sync that starts now,
    /// or right after the one already running. Any other copy downloads on the next sync. Rows that are
    /// not links are skipped.
    /// </summary>
    /// <param name="manager">The content table.</param>
    /// <param name="cache">The link cache.</param>
    /// <param name="request">Bulk items request.</param>
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
            return BadRequest("No items specified");
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
                // Kept by hand, so removing an Always Keep pick never turns it back into a link.
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

        // A cached file moves in seconds, so it is not left waiting for the scheduled sync. A sync already
        // running read its rows when it started, so another is queued to follow it.
        if (fromCache > 0 && _taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == "ServerSyncDownloadContent") is { } sync)
        {
            _taskManager.QueueScheduledTask(sync.ScheduledTask, new TaskOptions());
        }

        return Ok(new { Promoted = promoted, FromCache = fromCache, Skipped = skipped });
    }

    /// <summary>
    /// The full copies one server's Link mappings hold, for the Storage tab, with why each is a copy:
    /// Pick for an Always Keep pick, Hand for Keep Local Copy, Unknown for a copy from before this was
    /// recorded. Copies already going back to links are listed too.
    /// </summary>
    /// <param name="manager">The content table.</param>
    /// <param name="serverKey">The server's entry key.</param>
    /// <param name="search">Text to find in the paths.</param>
    /// <param name="skip">Rows to skip.</param>
    /// <param name="take">Rows to return, at most 200.</param>
    /// <returns>A page of rows.</returns>
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
    /// Turns kept copies in Link mappings back into links. Each is queued, and the next sync checks the
    /// source still has the file, writes the link, and moves the copy into the link cache, or removes it
    /// when the cache is off or cannot take it. A copy an Always Keep pick still covers is skipped, since
    /// the next refresh would only copy it again: the pick is removed instead.
    /// </summary>
    /// <param name="manager">The content table.</param>
    /// <param name="request">The row ids.</param>
    /// <returns>How many were queued, and why any were skipped.</returns>
    [HttpPost("KeptCopies/Unkeep")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult UnkeepCopies([FromServices] ContentSyncTableManager manager, [FromBody] Models.ContentSync.UnkeepRequest request)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (request?.Ids is not { Count: > 0 })
        {
            return BadRequest("No items specified");
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

    private static bool UnderRoot(string? path, string? root)
        => !string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(root)
            && path.Replace('\\', '/').StartsWith(root.Replace('\\', '/').TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);


    /// <summary>
    /// Marks items as synced after verifying the local file exists.
    /// Use this to resolve items that are stuck in Queued/Errored status
    /// when the local file is already present.
    /// </summary>
    /// <param name="request">Bulk items request.</param>
    /// <returns>Action result with synced and not-found counts.</returns>
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
            return BadRequest("No items specified");
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
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to mark item {ItemId} as synced", SanitizeForLog(itemId));
                notFoundCount++;
            }
        }

        return Ok(new { Synced = syncedCount, NotFound = notFoundCount });
    }

    /// <summary>
    /// Deletes items' files from the LOCAL server only. Each handled row is kept as Ignored.
    /// </summary>
    /// <param name="request">Bulk items request.</param>
    /// <returns>Action result with deletion counts.</returns>
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
            return BadRequest("No items specified");
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

            // The id-based delete removes whatever file the Jellyfin item
            // points at, and LocalItemId can go stale when a mapping's root
            // changes, so the RESOLVED path must pass the same checks the
            // tracked path did, and must actually be the tracked file.
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
                // Nothing to delete, so the row is kept as Ignored.
                _logger.LogWarning(
                    "SKIPPED DELETE: {FileName} - File not found in Jellyfin library or on disk, so the row is kept as Ignored. Source path: {SourcePath}",
                    sanitizedFileName,
                    SanitizeForLog(item.SourcePath));
                KeepAsIgnored(manager, item);
                skippedCount++;
                continue;
            }

            // The path actually deleted: the tracked one, or the Jellyfin item's own when the row tracks
            // none. The item's path passed the library check above, and gets the symlink check here.
            var targetPath = !string.IsNullOrEmpty(item.LocalPath) ? item.LocalPath : localItem?.Path;
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
                // The file may have been deleted externally (manually or by another process).
                // A folder item counts too, so a refused disc structure is reported as failed, never as gone.
                var stillExists = !string.IsNullOrEmpty(targetPath)
                    && (System.IO.File.Exists(targetPath) || System.IO.Directory.Exists(targetPath));
                if (!stillExists)
                {
                    // Deleted elsewhere, so the row is kept as Ignored.
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

    private static void KeepAsIgnored(ContentSyncTableManager manager, SyncItem item)
    {
        item.Status = SyncStatus.Ignored;
        item.PendingType = null;
        item.LocalItemId = null;
        item.StatusDate = DateTime.UtcNow;
        manager.Upsert(item);
    }
}
