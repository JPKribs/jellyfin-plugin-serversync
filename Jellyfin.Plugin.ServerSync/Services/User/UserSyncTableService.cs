using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Utilities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Per-record build helpers for User Sync. Each method builds one
/// <see cref="UserSyncItem"/> for one (mapping, category) tuple. Callers
/// pass the existing record (if any) to preserve LastSyncTime and Ignored
/// status. No DB writes — the caller (the Refresh task base) upserts.
/// </summary>
public class UserSyncTableService
{
    private readonly ILogger<UserSyncTableService> _logger;

    public UserSyncTableService(ILogger<UserSyncTableService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Builds a Policy sync item.
    /// </summary>
    public Task<UserSyncItem?> CreatePolicySyncItemAsync(
        UserMapping mapping,
        Jellyfin.Sdk.Generated.Models.UserDto sourceUser,
        MediaBrowser.Model.Dto.UserDto localUserDto,
        PluginConfiguration config,
        UserSyncItem? existingItem)
    {
        var sourcePolicy = UserSyncMergeService.ExtractPolicyJson(sourceUser.Policy);
        var localPolicy = UserSyncMergeService.ExtractPolicyJson(localUserDto.Policy);
        var mergedPolicy = UserSyncMergeService.ComputeMergedPolicy(sourcePolicy, config.LibraryMappings);

        var item = existingItem ?? new UserSyncItem
        {
            SourceUserId = mapping.SourceUserId,
            LocalUserId = mapping.LocalUserId,
            PropertyCategory = UserPropertyCategory.Policy
        };

        item.SourceUserName = sourceUser.Name;
        item.LocalUserName = localUserDto.Name;
        item.SourceValue = sourcePolicy;
        item.LocalValue = localPolicy;
        item.MergedValue = mergedPolicy;
        // Hash MergedValue (the value we'd actually apply), not SourceValue —
        // that way SyncedValueHash == hash(MergedValue) on the next refresh
        // means "the value we'd apply hasn't moved since last successful sync"
        // and we can short-circuit the deep JSON compare in HasChanges.
        item.SourceValueHash = HashUtilities.ComputeSha256Hash(mergedPolicy ?? string.Empty);
        item.StatusDate = DateTime.UtcNow;
        return Task.FromResult<UserSyncItem?>(item);
    }

    /// <summary>
    /// Builds a Configuration sync item. Source-wins (no merge).
    /// </summary>
    public Task<UserSyncItem?> CreateConfigurationSyncItemAsync(
        UserMapping mapping,
        Jellyfin.Sdk.Generated.Models.UserDto sourceUser,
        MediaBrowser.Model.Dto.UserDto localUserDto,
        UserSyncItem? existingItem)
    {
        var sourceConfig = UserSyncMergeService.ExtractConfigurationJson(sourceUser.Configuration);
        var localConfig = UserSyncMergeService.ExtractConfigurationJson(localUserDto.Configuration);

        var item = existingItem ?? new UserSyncItem
        {
            SourceUserId = mapping.SourceUserId,
            LocalUserId = mapping.LocalUserId,
            PropertyCategory = UserPropertyCategory.Configuration
        };

        item.SourceUserName = sourceUser.Name;
        item.LocalUserName = localUserDto.Name;
        item.SourceValue = sourceConfig;
        item.LocalValue = localConfig;
        item.MergedValue = sourceConfig;
        item.SourceValueHash = HashUtilities.ComputeSha256Hash(sourceConfig ?? string.Empty);
        item.StatusDate = DateTime.UtcNow;
        return Task.FromResult<UserSyncItem?>(item);
    }

    /// <summary>
    /// Builds a ProfileImage sync item. Fetches source image hash via the
    /// source client; computes local hash from the on-disk file when present.
    /// </summary>
    public async Task<UserSyncItem?> CreateProfileImageSyncItemAsync(
        UserMapping mapping,
        Jellyfin.Sdk.Generated.Models.UserDto sourceUser,
        Jellyfin.Database.Implementations.Entities.User localUser,
        SourceServerClient sourceClient,
        UserSyncItem? existingItem,
        CancellationToken cancellationToken)
    {
        var sourceUserId = Guid.Parse(mapping.SourceUserId);
        string? sourceImageHash = null;
        long? sourceImageSize = null;
        string? localImageHash = null;
        long? localImageSize = null;

        if (!string.IsNullOrEmpty(sourceUser.PrimaryImageTag))
        {
            (sourceImageHash, sourceImageSize) = await sourceClient.GetUserImageHashAndSizeAsync(sourceUserId, cancellationToken).ConfigureAwait(false);
        }

        if (localUser.ProfileImage != null && !string.IsNullOrEmpty(localUser.ProfileImage.Path))
        {
            try
            {
                if (File.Exists(localUser.ProfileImage.Path))
                {
                    localImageSize = new FileInfo(localUser.ProfileImage.Path).Length;
                    using var fileStream = File.OpenRead(localUser.ProfileImage.Path);
                    localImageHash = HashUtilities.ComputeSha256Hash(fileStream);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ProfileImage: failed to compute local hash for {User}", localUser.Username);
            }
        }

        var syncedImageHash = existingItem?.SyncedImageHash;
        if (string.IsNullOrEmpty(syncedImageHash)
            && !string.IsNullOrEmpty(sourceImageHash)
            && string.Equals(sourceImageHash, localImageHash, StringComparison.OrdinalIgnoreCase))
        {
            syncedImageHash = sourceImageHash;
        }

        var sourceDisplay = !string.IsNullOrEmpty(sourceImageHash)
            ? $"{FormatUtilities.FormatBytes(sourceImageSize ?? 0)} ({sourceImageHash[..8]}...)"
            : "No image";
        var localDisplay = !string.IsNullOrEmpty(localImageHash)
            ? $"{FormatUtilities.FormatBytes(localImageSize ?? 0)} ({localImageHash[..8]}...)"
            : "No image";

        var item = existingItem ?? new UserSyncItem
        {
            SourceUserId = mapping.SourceUserId,
            LocalUserId = mapping.LocalUserId,
            PropertyCategory = UserPropertyCategory.ProfileImage
        };

        item.SourceUserName = sourceUser.Name;
        item.LocalUserName = localUser.Username;
        item.SourceValue = sourceDisplay;
        item.LocalValue = localDisplay;
        item.MergedValue = sourceDisplay;
        item.SourceImageHash = sourceImageHash;
        item.LocalImageHash = localImageHash;
        item.SyncedImageHash = syncedImageHash;
        item.SourceImageSize = sourceImageSize;
        item.LocalImageSize = localImageSize;
        item.SyncedImageSize = existingItem?.SyncedImageSize;
        item.StatusDate = DateTime.UtcNow;
        return item;
    }
}
