using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Extensions;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Services.UserSync;
using JPKribs.Jellyfin.Base;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// The Users routes of the Sync tab.
/// </summary>
public partial class ConfigurationController
{
    /// <summary>
    /// Counts the user mappings in each status, one per visible row, so the counter matches the list below it.
    /// </summary>
    /// <param name="manager">Reads and writes the Users sync table.</param>
    /// <returns>The row counts by status.</returns>
    [HttpGet("UserStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<BaseSyncStatusResponse> GetUserSyncStatus([FromServices] UserSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        var response = new BaseSyncStatusResponse
        {
            Pending = manager.CountUserMappingsByStatus(SyncStatus.Pending),
            Queued = manager.CountUserMappingsByStatus(SyncStatus.Queued),
            Synced = manager.CountUserMappingsByStatus(SyncStatus.Synced),
            Errored = manager.CountUserMappingsByStatus(SyncStatus.Errored),
            Ignored = manager.CountUserMappingsByStatus(SyncStatus.Ignored)
        };
        PopulateLastFailure(response, "User");
        return Ok(response);
    }

    /// <summary>
    /// Queues the selected Users rows. With no IDs and a status, it queues every row in that status, which is how a retry reaches every errored row.
    /// </summary>
    /// <param name="request">The IDs of the selected rows, or a status to queue every row of.</param>
    /// <param name="manager">Reads and writes the Users sync table.</param>
    /// <returns>How many rows changed and which did not, or 400 when none was selected.</returns>
    [HttpPost("UserItems/Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult QueueUserSyncItems(
        [FromBody] BulkUserSyncItemsRequest request,
        [FromServices] UserSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (request != null && (request.Ids == null || request.Ids.Count == 0) && !string.IsNullOrEmpty(request.Status))
        {
            return QueueAllWithStatus(manager, request.Status, "QueueUserSyncItems");
        }

        if (request?.Ids == null || request.Ids.Count == 0)
        {
            return BadRequest("Select the items.");
        }

        try
        {
            var (updated, notFound) = manager.BulkUpdateStatusWithDetails(request.Ids, SyncStatus.Queued);
            return Ok(BuildBulkResult(updated, request.Ids.Count, notFound, "QueueUserSyncItems"));
        }
        // Any failure becomes a 500 the page can show.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue user sync items by IDs");
            return StatusCode(500, new { Error = "Could not queue the items. See the server log." });
        }
    }

    /// <summary>
    /// Lists a page of user mappings, one row per pair of source user and local user.
    /// </summary>
    /// <param name="manager">Reads and writes the Users sync table.</param>
    /// <param name="search">Text to find in the item names, or null for no filter.</param>
    /// <param name="status">The item status to list, or null for every status.</param>
    /// <param name="skip">How many rows to pass over before the page starts.</param>
    /// <param name="take">The most rows the page holds, clamped to 200.</param>
    /// <returns>The requested page of users.</returns>
    [HttpGet("UserSyncUsers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PagedResult<UserSyncUserDto>> GetUserSyncUsers(
        [FromServices] UserSyncTableManager manager,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50)
    {
        ArgumentNullException.ThrowIfNull(manager);

        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);

        SyncStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<SyncStatus>(status, out var parsed) && Enum.IsDefined(parsed))
        {
            statusFilter = parsed;
        }

        var result = manager.SearchUserMappingsPaginated(search, statusFilter, skip, take);

        var config = _configManager.Configuration;
        var dtos = result.Items.Select(g => MapToUserSyncUserDto(
            g.SourceUserId, g.LocalUserId, g.SourceUserName, g.LocalUserName, g.Items.ToList(), config)).ToList();

        return Ok(new PagedResult<UserSyncUserDto>(
            dtos,
            result.TotalCount));
    }

    /// <summary>
    /// Reads every row of one user mapping.
    /// </summary>
    /// <param name="sourceUserId">The user's id on the source.</param>
    /// <param name="localUserId">The user's id on this server.</param>
    /// <param name="manager">Reads and writes the Users sync table.</param>
    /// <returns>The user's rows, or 404 when there are none.</returns>
    [HttpGet("UserSyncUsers/{sourceUserId}/{localUserId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<UserSyncUserDetailDto> GetUserSyncUserDetail(
        string sourceUserId,
        string localUserId,
        [FromServices] UserSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);

        var items = manager.GetByUserMapping(sourceUserId, localUserId);
        if (items.Count == 0)
        {
            return NotFound();
        }

        return Ok(MapToUserSyncUserDetailDto(sourceUserId, localUserId, items.ToList(), _configManager.Configuration));
    }

    /// <summary>
    /// Marks every row of the selected user mappings as Ignored.
    /// </summary>
    /// <param name="request">The selected user mappings.</param>
    /// <param name="manager">Reads and writes the Users sync table.</param>
    /// <returns>How many rows changed, or 400 when no mapping was selected.</returns>
    [HttpPost("UserSyncUsers/Ignore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult IgnoreUserSyncUsers(
        [FromBody] BulkUserMappingsRequest request,
        [FromServices] UserSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);

        if (request?.UserMappings == null || request.UserMappings.Count == 0)
        {
            return BadRequest("Select the user mappings.");
        }

        var mappings = request.UserMappings.Select(m => (m.SourceUserId, m.LocalUserId));
        var successCount = manager.BulkUpdateStatusByMappings(mappings, SyncStatus.Ignored);
        return Ok(new { Updated = successCount });
    }

    /// <summary>
    /// Queues every row of the selected user mappings.
    /// </summary>
    /// <param name="request">The selected user mappings.</param>
    /// <param name="manager">Reads and writes the Users sync table.</param>
    /// <returns>How many rows changed, or 400 when no mapping was selected.</returns>
    [HttpPost("UserSyncUsers/Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult QueueUserSyncUsers(
        [FromBody] BulkUserMappingsRequest request,
        [FromServices] UserSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);

        if (request?.UserMappings == null || request.UserMappings.Count == 0)
        {
            return BadRequest("Select the user mappings.");
        }

        var mappings = request.UserMappings.Select(m => (m.SourceUserId, m.LocalUserId));
        var successCount = manager.BulkUpdateStatusByMappings(mappings, SyncStatus.Queued);
        return Ok(new { Updated = successCount });
    }

    /// <summary>Builds the list row for one user mapping from its rows.</summary>
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
        var firstItem = items.FirstOrDefault();

        var dto = new UserSyncUserDto
        {
            SourceUserId = sourceUserId,
            LocalUserId = localUserId,
            SourceUserName = sourceUserName ?? firstItem?.SourceUserName,
            LocalUserName = localUserName ?? firstItem?.LocalUserName,
            SourceServerUrl = config.ResolveServer(firstItem?.ServerKey)?.BrowserUrl,
            ServerKey = firstItem?.ServerKey,
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
                .OfType<DateTime>().DefaultIfEmpty().Max(),
            ErrorMessage = string.Join(". ", new[] { policyItem?.Reason, configItem?.Reason, imageItem?.Reason }
                .Where(e => !string.IsNullOrEmpty(e))
                .Select(e => e!.TrimEnd('.', ' ')))
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

    /// <summary>Builds the detail view of one user mapping from its rows.</summary>
    private static UserSyncUserDetailDto MapToUserSyncUserDetailDto(
        string sourceUserId,
        string localUserId,
        List<UserSyncItem> items,
        PluginConfiguration config)
    {
        var policyItem = items.FirstOrDefault(i => i.PropertyCategory == UserPropertyCategory.Policy);
        var configItem = items.FirstOrDefault(i => i.PropertyCategory == UserPropertyCategory.Configuration);
        var imageItem = items.FirstOrDefault(i => i.PropertyCategory == UserPropertyCategory.ProfileImage);
        var firstItem = items.FirstOrDefault();

        var dto = new UserSyncUserDetailDto
        {
            SourceUserId = sourceUserId,
            LocalUserId = localUserId,
            SourceUserName = firstItem?.SourceUserName,
            LocalUserName = firstItem?.LocalUserName,
            PolicyItem = policyItem?.ToDto(),
            ConfigurationItem = configItem?.ToDto(),
            ProfileImageItem = imageItem?.ToDto(),
            PolicyEnabled = config.UserSyncPolicy,
            ConfigurationEnabled = config.UserSyncConfiguration,
            ProfileImageEnabled = config.UserSyncProfileImage,
            LastSyncTime = new[] { policyItem?.LastSyncTime, configItem?.LastSyncTime, imageItem?.LastSyncTime }
                .OfType<DateTime>().DefaultIfEmpty().Max(),
            ErrorMessage = string.Join(". ", new[] { policyItem?.Reason, configItem?.Reason, imageItem?.Reason }
                .Where(e => !string.IsNullOrEmpty(e))
                .Select(e => e!.TrimEnd('.', ' ')))
        };

        dto.OverallStatus = ComputeOverallStatus(policyItem?.Status, configItem?.Status, imageItem?.Status);

        if (string.IsNullOrEmpty(dto.ErrorMessage))
        {
            dto.ErrorMessage = null;
        }

        return dto;
    }

    /// <summary>Describes how many policy, settings, and image changes a user mapping has.</summary>
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

    /// <summary>Reads the number of differences from a changes summary, or 1 when it names none.</summary>
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
