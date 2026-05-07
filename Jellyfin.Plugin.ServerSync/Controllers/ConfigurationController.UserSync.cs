using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// User sync endpoints for Server Sync plugin.
/// </summary>
public partial class ConfigurationController
{
    /// <summary>
    /// Gets a single user sync item by its ID.
    /// </summary>
    [HttpGet("UserItems/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<UserSyncItemDto> GetUserSyncItemById(long id, [FromServices] UserSyncTableManager manager)
    {
        var item = manager.GetById(id);
        if (item == null)
        {
            return NotFound("User sync item not found");
        }

        var config = _configManager.Configuration;
        var url = !string.IsNullOrEmpty(config.SourceServerExternalUrl) ? config.SourceServerExternalUrl : config.SourceServerUrl;
        return Ok(MapToUserSyncItemDto(item, url, config.SourceServerApiKey));
    }

    /// <summary>
    /// Gets paginated user sync items with optional search and status filter.
    /// </summary>
    [HttpGet("UserItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PaginatedResult<UserSyncItemDto>> GetUserSyncItems(
        [FromServices] UserSyncTableManager manager,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? sourceUserId = null,
        [FromQuery] string? propertyCategory = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50)
    {
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);

        SyncStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<SyncStatus>(status, out var parsed))
        {
            statusFilter = parsed;
        }

        var page = (skip / Math.Max(1, take)) + 1;
        var result = manager.Paginate(new PaginationRequest
        {
            Page = page,
            PageSize = take,
            SearchTerm = search,
            StatusFilter = statusFilter
        });

        // Manager.Paginate doesn't filter by sourceUserId/propertyCategory; apply
        // those in-memory. The page may shrink as a result — acceptable since
        // these are rarely used together with search/status filters in the UI.
        var filtered = result.Items.AsEnumerable();
        if (!string.IsNullOrEmpty(sourceUserId))
        {
            filtered = filtered.Where(i => string.Equals(i.SourceUserId, sourceUserId, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(propertyCategory))
        {
            filtered = filtered.Where(i => string.Equals(i.PropertyCategory, propertyCategory, StringComparison.OrdinalIgnoreCase));
        }

        var config = _configManager.Configuration;
        var url = !string.IsNullOrEmpty(config.SourceServerExternalUrl) ? config.SourceServerExternalUrl : config.SourceServerUrl;
        return Ok(new PaginatedResult<UserSyncItemDto>
        {
            Items = filtered.Select(i => MapToUserSyncItemDto(i, url, config.SourceServerApiKey)).ToList(),
            TotalCount = result.TotalCount,
            Skip = skip,
            Take = take
        });
    }

    /// <summary>
    /// Gets user sync status counts. Counts distinct user mappings (one per
    /// visible row) rather than raw category-row count, so the header
    /// counter matches the list below. See
    /// <see cref="UserSyncTableManager.CountUserMappingsByStatus"/>.
    /// </summary>
    [HttpGet("UserStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<BaseSyncStatusResponse> GetUserSyncStatus([FromServices] UserSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return Ok(new BaseSyncStatusResponse
        {
            Pending = manager.CountUserMappingsByStatus(SyncStatus.Pending),
            Queued = manager.CountUserMappingsByStatus(SyncStatus.Queued),
            Synced = manager.CountUserMappingsByStatus(SyncStatus.Synced),
            Errored = manager.CountUserMappingsByStatus(SyncStatus.Errored),
            Ignored = manager.CountUserMappingsByStatus(SyncStatus.Ignored)
        });
    }

    /// <summary>
    /// Updates the status of a user sync item.
    /// </summary>
    [HttpPost("UserItems/UpdateStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult UpdateUserSyncItemStatus(
        [FromBody] UpdateUserSyncItemStatusRequest request,
        [FromServices] UserSyncTableManager manager)
    {
        if (!Enum.TryParse<SyncStatus>(request.Status, out var status))
        {
            return BadRequest("Invalid status value");
        }

        manager.UpdateStatus(request.Id, status);
        return Ok(new { Success = true });
    }

    /// <summary>
    /// Moves user sync items to Queued status.
    /// </summary>
    [HttpPost("UserItems/Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult QueueUserSyncItems(
        [FromBody] BulkUserSyncItemsRequest request,
        [FromServices] UserSyncTableManager manager)
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
            _logger.LogWarning(ex, "Failed to queue user sync items by IDs");
        }

        return Ok(new { Updated = successCount });
    }

    /// <summary>
    /// Marks user sync items as ignored.
    /// </summary>
    [HttpPost("UserItems/Ignore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult IgnoreUserSyncItems(
        [FromBody] BulkUserSyncItemsRequest request,
        [FromServices] UserSyncTableManager manager)
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
            _logger.LogWarning(ex, "Failed to ignore user sync items by IDs");
        }

        return Ok(new { Updated = successCount });
    }

    /// <summary>
    /// Manually triggers the refresh user sync table task.
    /// </summary>
    [HttpPost("TriggerUserRefresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerUserRefresh()
        => ExecuteScheduledTaskByKey("ServerSyncRefreshUserTable", "User refresh task started", "User refresh task not found");

    /// <summary>
    /// Manually triggers the sync missing user data task.
    /// </summary>
    [HttpPost("TriggerUserSync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerUserSync()
        => ExecuteScheduledTaskByKey("ServerSyncMissingUserData", "User sync task started", "User sync task not found");

    /// <summary>
    /// Resets the user sync database, removing all tracked user sync items.
    /// </summary>
    [HttpPost("ResetUserSyncDatabase")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ResetUserSyncDatabase([FromServices] UserSyncTableManager manager)
    {
        try
        {
            var deleted = manager.ResetTable();
            _logger.LogInformation("User sync database has been reset, {Count} rows deleted", deleted);
            return Ok(new { Success = true, Message = "User sync database reset successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset user sync database");
            return StatusCode(500, new { Success = false, Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Gets paginated list of user sync users (consolidated view).
    /// Groups items by (SourceUserId, LocalUserId) showing one row per user.
    /// </summary>
    [HttpGet("UserSyncUsers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PaginatedResult<UserSyncUserDto>> GetUserSyncUsers(
        [FromServices] UserSyncTableManager manager,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50)
    {
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);

        SyncStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<SyncStatus>(status, out var parsed))
        {
            statusFilter = parsed;
        }

        var page = (skip / Math.Max(1, take)) + 1;
        var result = manager.PaginateUserMappings(new PaginationRequest
        {
            Page = page,
            PageSize = take,
            SearchTerm = search,
            StatusFilter = statusFilter
        });

        var config = _configManager.Configuration;
        var dtos = result.Items.Select(g => MapToUserSyncUserDto(
            g.SourceUserId, g.LocalUserId, g.SourceUserName, g.LocalUserName, g.Items.ToList(), config)).ToList();

        return Ok(new PaginatedResult<UserSyncUserDto>
        {
            Items = dtos,
            TotalCount = result.TotalCount
        });
    }

    /// <summary>
    /// Gets detailed information for a specific user mapping.
    /// </summary>
    [HttpGet("UserSyncUsers/{sourceUserId}/{localUserId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<UserSyncUserDetailDto> GetUserSyncUserDetail(
        string sourceUserId,
        string localUserId,
        [FromServices] UserSyncTableManager manager)
    {
        var items = manager.GetByUserMapping(sourceUserId, localUserId);
        if (items.Count == 0)
        {
            return NotFound();
        }

        var config = _configManager.Configuration;
        var dto = MapToUserSyncUserDetailDto(sourceUserId, localUserId, items.ToList(), config);
        return Ok(dto);
    }

    /// <summary>
    /// Ignores all categories for specified user mappings.
    /// </summary>
    [HttpPost("UserSyncUsers/Ignore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult IgnoreUserSyncUsers(
        [FromBody] BulkUserMappingsRequest request,
        [FromServices] UserSyncTableManager manager)
    {
        if (request?.UserMappings == null || request.UserMappings.Count == 0)
        {
            return BadRequest("No user mappings specified");
        }

        var mappings = request.UserMappings.Select(m => (m.SourceUserId, m.LocalUserId));
        var successCount = manager.BulkUpdateStatusByMappings(mappings, SyncStatus.Ignored);
        return Ok(new { Updated = successCount });
    }

    /// <summary>
    /// Queues all categories for specified user mappings.
    /// </summary>
    [HttpPost("UserSyncUsers/Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult QueueUserSyncUsers(
        [FromBody] BulkUserMappingsRequest request,
        [FromServices] UserSyncTableManager manager)
    {
        if (request?.UserMappings == null || request.UserMappings.Count == 0)
        {
            return BadRequest("No user mappings specified");
        }

        var mappings = request.UserMappings.Select(m => (m.SourceUserId, m.LocalUserId));
        var successCount = manager.BulkUpdateStatusByMappings(mappings, SyncStatus.Queued);
        return Ok(new { Updated = successCount });
    }

    // ===== DTO mapping helpers (unchanged from prior version) =====

    private static UserSyncItemDto MapToUserSyncItemDto(UserSyncItem item, string? sourceServerUrl = null, string? sourceServerApiKey = null)
    {
        return new UserSyncItemDto
        {
            Id = item.Id,
            SourceUserId = item.SourceUserId,
            LocalUserId = item.LocalUserId,
            SourceUserName = item.SourceUserName,
            LocalUserName = item.LocalUserName,
            PropertyCategory = item.PropertyCategory,
            SourceValue = item.SourceValue,
            LocalValue = item.LocalValue,
            MergedValue = item.MergedValue,
            SourceImageSize = item.SourceImageSize,
            LocalImageSize = item.LocalImageSize,
            SourceImageSizeFormatted = item.SourceImageSize.HasValue ? FormatUtilities.FormatBytes(item.SourceImageSize.Value) : null,
            LocalImageSizeFormatted = item.LocalImageSize.HasValue ? FormatUtilities.FormatBytes(item.LocalImageSize.Value) : null,
            HasChanges = item.HasChanges,
            ChangesSummary = item.ChangesSummary,
            SourceServerUrl = sourceServerUrl,
            SourceServerApiKey = sourceServerApiKey,
            Status = item.Status.ToString(),
            StatusDate = item.StatusDate,
            LastSyncTime = item.LastSyncTime,
            ErrorMessage = item.Reason
        };
    }

    private static UserSyncUserDto MapToUserSyncUserDto(
        string sourceUserId,
        string localUserId,
        string? sourceUserName,
        string? localUserName,
        List<UserSyncItem> items,
        PluginConfiguration config)
    {
        var policyItem = items.FirstOrDefault(i => i.PropertyCategory == UserPropertyCategory.Policy);
        var configItem = items.FirstOrDefault(i => i.PropertyCategory == UserPropertyCategory.Configuration);
        var imageItem = items.FirstOrDefault(i => i.PropertyCategory == UserPropertyCategory.ProfileImage);

        var dto = new UserSyncUserDto
        {
            SourceUserId = sourceUserId,
            LocalUserId = localUserId,
            SourceUserName = sourceUserName ?? items.FirstOrDefault()?.SourceUserName,
            LocalUserName = localUserName ?? items.FirstOrDefault()?.LocalUserName,
            SourceServerUrl = !string.IsNullOrEmpty(config.SourceServerExternalUrl) ? config.SourceServerExternalUrl : config.SourceServerUrl,
            SourceServerApiKey = config.SourceServerApiKey,
            PolicyId = policyItem?.Id,
            ConfigurationId = configItem?.Id,
            ProfileImageId = imageItem?.Id,
            PolicyStatus = policyItem?.Status.ToString(),
            ConfigurationStatus = configItem?.Status.ToString(),
            ProfileImageStatus = imageItem?.Status.ToString(),
            PolicyHasChanges = policyItem?.HasChanges ?? false,
            ConfigurationHasChanges = configItem?.HasChanges ?? false,
            ProfileImageHasChanges = imageItem?.HasChanges ?? false,
            PolicyChangesSummary = policyItem?.ChangesSummary,
            ConfigurationChangesSummary = configItem?.ChangesSummary,
            ProfileImageChangesSummary = imageItem?.ChangesSummary,
            HasChanges = (policyItem?.HasChanges ?? false) || (configItem?.HasChanges ?? false) || (imageItem?.HasChanges ?? false),
            LastSyncTime = new[] { policyItem?.LastSyncTime, configItem?.LastSyncTime, imageItem?.LastSyncTime }
                .Where(t => t.HasValue).Select(t => t!.Value).DefaultIfEmpty().Max(),
            ErrorMessage = string.Join("; ", new[] { policyItem?.Reason, configItem?.Reason, imageItem?.Reason }
                .Where(e => !string.IsNullOrEmpty(e)))
        };

        dto.OverallStatus = ComputeOverallStatus(policyItem?.Status, configItem?.Status, imageItem?.Status);
        dto.TotalChanges = ComputeTotalChangesDisplay(
            policyItem?.HasChanges ?? false, policyItem?.ChangesSummary,
            configItem?.HasChanges ?? false, configItem?.ChangesSummary,
            imageItem?.HasChanges ?? false);

        if (string.IsNullOrEmpty(dto.ErrorMessage))
        {
            dto.ErrorMessage = null;
        }

        return dto;
    }

    private static UserSyncUserDetailDto MapToUserSyncUserDetailDto(
        string sourceUserId,
        string localUserId,
        List<UserSyncItem> items,
        PluginConfiguration config)
    {
        var policyItem = items.FirstOrDefault(i => i.PropertyCategory == UserPropertyCategory.Policy);
        var configItem = items.FirstOrDefault(i => i.PropertyCategory == UserPropertyCategory.Configuration);
        var imageItem = items.FirstOrDefault(i => i.PropertyCategory == UserPropertyCategory.ProfileImage);

        var dto = new UserSyncUserDetailDto
        {
            SourceUserId = sourceUserId,
            LocalUserId = localUserId,
            SourceUserName = items.FirstOrDefault()?.SourceUserName,
            LocalUserName = items.FirstOrDefault()?.LocalUserName,
            PolicyItem = policyItem != null ? MapToUserSyncItemDto(policyItem) : null,
            ConfigurationItem = configItem != null ? MapToUserSyncItemDto(configItem) : null,
            ProfileImageItem = imageItem != null ? MapToUserSyncItemDto(imageItem) : null,
            PolicyEnabled = config.UserSyncPolicy,
            ConfigurationEnabled = config.UserSyncConfiguration,
            ProfileImageEnabled = config.UserSyncProfileImage,
            LastSyncTime = new[] { policyItem?.LastSyncTime, configItem?.LastSyncTime, imageItem?.LastSyncTime }
                .Where(t => t.HasValue).Select(t => t!.Value).DefaultIfEmpty().Max(),
            ErrorMessage = string.Join("; ", new[] { policyItem?.Reason, configItem?.Reason, imageItem?.Reason }
                .Where(e => !string.IsNullOrEmpty(e)))
        };

        dto.OverallStatus = ComputeOverallStatus(policyItem?.Status, configItem?.Status, imageItem?.Status);

        if (string.IsNullOrEmpty(dto.ErrorMessage))
        {
            dto.ErrorMessage = null;
        }

        return dto;
    }

    private static string ComputeTotalChangesDisplay(
        bool policyHasChanges, string? policySummary,
        bool configHasChanges, string? configSummary,
        bool imageHasChanges)
    {
        var parts = new List<string>();
        if (policyHasChanges)
        {
            var count = ExtractChangeCount(policySummary);
            parts.Add(count > 1 ? $"{count} policy" : "1 policy");
        }

        if (configHasChanges)
        {
            var count = ExtractChangeCount(configSummary);
            parts.Add(count > 1 ? $"{count} config" : "1 config");
        }

        if (imageHasChanges)
        {
            parts.Add("1 image");
        }

        return parts.Count == 0 ? "No Changes" : string.Join(", ", parts);
    }

    private static int ExtractChangeCount(string? summary)
    {
        if (string.IsNullOrEmpty(summary))
        {
            return 1;
        }

        var match = Regex.Match(summary, @"(\d+)\s+difference");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var count))
        {
            return count;
        }

        return 1;
    }
}
