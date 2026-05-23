using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Per-record build helpers for User Sync. Builds one
/// <see cref="UserSyncItem"/> for one (mapping, category) tuple, preserving
/// LastSyncTime and Ignored status from any existing record. No DB writes —
/// the caller (the Refresh task base) upserts.
/// </summary>
[PluginService(ServiceLifetime.Transient)]
public class UserSyncTableService
{
    private readonly ILogger<UserSyncTableService> _logger;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public UserSyncTableService(ILogger<UserSyncTableService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Builds a <see cref="UserSyncItem"/> for one (mapping, category) tuple.
    /// Returns null for an unknown category. The category-specific helpers
    /// are private — callers go through this single entry point so adding a
    /// category is a switch-arm addition rather than a new public method.
    /// </summary>
    public async Task<UserSyncItem?> BuildRecordAsync(
        UserMapping mapping,
        string category,
        Jellyfin.Sdk.Generated.Models.UserDto sourceUser,
        MediaBrowser.Model.Dto.UserDto localUserDto,
        Jellyfin.Database.Implementations.Entities.User localUser,
        SourceServerClient sourceClient,
        PluginConfiguration config,
        UserSyncItem? existingItem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(sourceUser);
        ArgumentNullException.ThrowIfNull(sourceClient);
        ArgumentNullException.ThrowIfNull(config);

        return category switch
        {
            UserPropertyCategory.Policy => BuildPolicyItem(mapping, sourceUser, localUserDto, config, existingItem),
            UserPropertyCategory.Configuration => BuildConfigurationItem(mapping, sourceUser, localUserDto, existingItem),
            UserPropertyCategory.ProfileImage => await BuildProfileImageItemAsync(mapping, sourceUser, localUser, sourceClient, existingItem, cancellationToken).ConfigureAwait(false),
            _ => null
        };
    }

    private static UserSyncItem BuildPolicyItem(
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
        // UpdateMergedValue stores MergedValue (the value we'd apply) on the
        // backing SyncableValue and recomputes the source-hash via the
        // comparator. SyncedHash matches that format after a successful sync,
        // so the fast-path skip on the next Refresh stays consistent.
        item.UpdateMergedValue(mergedPolicy);
        item.StatusDate = DateTime.UtcNow;
        return item;
    }

    private static UserSyncItem BuildConfigurationItem(
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
        // Configuration is pure source-wins, so MergedValue == sourceConfig.
        item.UpdateMergedValue(sourceConfig);
        item.StatusDate = DateTime.UtcNow;
        return item;
    }

    private async Task<UserSyncItem> BuildProfileImageItemAsync(
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
