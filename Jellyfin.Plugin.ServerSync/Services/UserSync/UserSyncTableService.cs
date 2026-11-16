using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.UserSync;

/// <summary>
/// Builds one <see cref="UserSyncItem"/> for one user mapping and category, on top of the stored row when there is one.
/// It writes nothing, since the scan stores the row.
/// </summary>
[PluginService(ServiceLifetime.Transient)]
public class UserSyncTableService
{
    private readonly ILogger<UserSyncTableService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserSyncTableService"/> class.
    /// </summary>
    /// <param name="logger">Writes log entries.</param>
    public UserSyncTableService(ILogger<UserSyncTableService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Builds the row for one user mapping and one category.
    /// </summary>
    /// <param name="mapping">The user mapping the row belongs to.</param>
    /// <param name="category">The property category: Policy, Configuration, or ProfileImage.</param>
    /// <param name="sourceUser">The user as the source returned it.</param>
    /// <param name="localUserDto">The local user as Jellyfin's API shows it, read for its policy and configuration.</param>
    /// <param name="localUser">The local user's entity, read for its profile image.</param>
    /// <param name="sourceClient">The client for the source server, used to read the source profile image.</param>
    /// <param name="config">The plugin configuration, read for library mappings when none are passed.</param>
    /// <param name="existingItem">The stored row to build on, or null.</param>
    /// <param name="cancellationToken">Cancels the read of the source profile image.</param>
    /// <param name="libraryMappings">The enabled library mappings, or null to read them from the configuration.</param>
    /// <param name="bothWays">The conflict rule for a Sync server, or null.</param>
    /// <returns>The row, or null for an unknown category.</returns>
    public async Task<UserSyncItem?> BuildRecordAsync(
        UserMapping mapping,
        string category,
        Jellyfin.Sdk.Generated.Models.UserDto sourceUser,
        MediaBrowser.Model.Dto.UserDto localUserDto,
        Jellyfin.Database.Implementations.Entities.User localUser,
        SourceServerClient sourceClient,
        PluginConfiguration config,
        UserSyncItem? existingItem,
        CancellationToken cancellationToken,
        List<LibraryMapping>? libraryMappings = null,
        UserConflictRule? bothWays = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(sourceUser);
        ArgumentNullException.ThrowIfNull(localUserDto);
        ArgumentNullException.ThrowIfNull(localUser);
        ArgumentNullException.ThrowIfNull(sourceClient);
        ArgumentNullException.ThrowIfNull(config);
        libraryMappings ??= config.GetEnabledLibraryMappings();

        return category switch
        {
            UserPropertyCategory.Policy => BuildPolicyItem(mapping, sourceUser, localUserDto, libraryMappings, existingItem, bothWays),
            UserPropertyCategory.Configuration => BuildConfigurationItem(mapping, sourceUser, localUserDto, existingItem, bothWays),
            UserPropertyCategory.ProfileImage => await BuildProfileImageItemAsync(mapping, sourceUser, localUser, sourceClient, existingItem, config.DeepImageVerification, cancellationToken).ConfigureAwait(false),
            _ => null
        };
    }

    /// <summary>
    /// Builds the Policy row, with the source's library ids translated and a Sync server's conflict settled by the rule.
    /// </summary>
    private static UserSyncItem BuildPolicyItem(
        UserMapping mapping,
        Jellyfin.Sdk.Generated.Models.UserDto sourceUser,
        MediaBrowser.Model.Dto.UserDto localUserDto,
        List<LibraryMapping> libraryMappings,
        UserSyncItem? existingItem,
        UserConflictRule? bothWays)
    {
        var sourcePolicy = UserSyncMergeService.ExtractPolicyJson(sourceUser.Policy);
        var localPolicy = UserSyncMergeService.ExtractPolicyJson(localUserDto.Policy);
        var mergedPolicy = UserSyncMergeService.ComputeMergedPolicy(sourcePolicy, libraryMappings);
        var keep = false;
        if (bothWays is { } rule)
        {
            (mergedPolicy, keep) = SettleBothWays(existingItem, mergedPolicy, localPolicy, other => UserPolicyCombiner.Combine(other, localPolicy, rule));
        }

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
        item.UpdateMergedValue(mergedPolicy);
        item.KeepAgreedValue = keep;
        item.StatusDate = DateTime.UtcNow;
        return item;
    }

    /// <summary>
    /// Decides what one category becomes between this server and a Sync server, from which of the two changed it since they last agreed.
    /// The side that changed alone wins, <paramref name="combine"/> decides when both changed, and the other server wins before any agreement.
    /// </summary>
    /// <param name="seen">The stored row, as the last scan left it, or null.</param>
    /// <param name="theirs">The other server's value in this server's terms.</param>
    /// <param name="mine">This server's value as it stands.</param>
    /// <param name="combine">Combines the other server's value with this server's when both changed.</param>
    /// <returns>The value to apply, and whether it is this server's own value being kept.</returns>
    public static (string? Value, bool Keep) SettleBothWays(UserSyncItem? seen, string? theirs, string? mine, Func<string?, string?> combine)
    {
        ArgumentNullException.ThrowIfNull(combine);
        var comparator = new Models.Common.Comparators.JsonBlobComparator();
        if (seen?.SyncedValueHash is not { Length: > 0 } agreed || comparator.Equals(theirs, mine))
        {
            return (theirs, false);
        }

        var theyChanged = !string.Equals(comparator.ComputeHash(theirs), agreed, StringComparison.Ordinal);
        if (!theyChanged)
        {
            return (mine, true);
        }

        // A synced row that differs from the agreed value is one this server kept.
        var keptBefore = seen.Status == Models.Common.SyncStatus.Synced
            && !string.Equals(comparator.ComputeHash(seen.MergedValue), agreed, StringComparison.Ordinal);
        var iChanged = keptBefore || !comparator.Equals(seen.LocalValue, mine);
        return (iChanged ? combine(theirs) : theirs, false);
    }

    /// <summary>
    /// Builds the Configuration row, where the other server's value stands when both servers changed it.
    /// </summary>
    private static UserSyncItem BuildConfigurationItem(
        UserMapping mapping,
        Jellyfin.Sdk.Generated.Models.UserDto sourceUser,
        MediaBrowser.Model.Dto.UserDto localUserDto,
        UserSyncItem? existingItem,
        UserConflictRule? bothWays)
    {
        var sourceConfig = UserSyncMergeService.ExtractConfigurationJson(sourceUser.Configuration);
        var localConfig = UserSyncMergeService.ExtractConfigurationJson(localUserDto.Configuration);
        var applied = sourceConfig;
        var keep = false;
        if (bothWays is not null)
        {
            (applied, keep) = SettleBothWays(existingItem, sourceConfig, localConfig, other => other);
        }

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
        item.UpdateMergedValue(applied);
        item.KeepAgreedValue = keep;
        item.StatusDate = DateTime.UtcNow;
        return item;
    }

    /// <summary>
    /// Builds the ProfileImage row from the hash and size of each server's image.
    /// A source image whose tag is unchanged keeps the hash already read, and the deep check reads it again whatever the tag says.
    /// A source image that cannot be read keeps its last known hash, and with none to keep the error fails the row.
    /// </summary>
    private async Task<UserSyncItem> BuildProfileImageItemAsync(
        UserMapping mapping,
        Jellyfin.Sdk.Generated.Models.UserDto sourceUser,
        Jellyfin.Database.Implementations.Entities.User localUser,
        SourceServerClient sourceClient,
        UserSyncItem? existingItem,
        bool deepVerification,
        CancellationToken cancellationToken)
    {
        var sourceUserId = Guid.Parse(mapping.SourceUserId);
        var sourceImageTag = sourceUser.PrimaryImageTag;
        string? sourceImageHash = null;
        long? sourceImageSize = null;
        string? localImageHash = null;
        long? localImageSize = null;

        if (!deepVerification && ImageUtilities.PictureTagUnchanged(sourceImageTag, existingItem?.SourceValueHash, existingItem?.SourceImageHash))
        {
            // The tag changes whenever the source's image is written, so an unchanged tag keeps the hash already read and saves the download.
            sourceImageHash = existingItem!.SourceImageHash;
            sourceImageSize = existingItem.SourceImageSize;
        }
        else if (!string.IsNullOrEmpty(sourceImageTag))
        {
            try
            {
                (sourceImageHash, sourceImageSize) = await sourceClient.GetUserImageHashAndSizeAsync(sourceUserId, cancellationToken).ConfigureAwait(false);
            }
            // A null hash would read as a removed image and clear the local one.
            catch (Exception ex) when (ex is not OperationCanceledException && !string.IsNullOrEmpty(existingItem?.SourceImageHash))
            {
                _logger.LogWarning(ex, "ProfileImage: could not read the source image for {User}, keeping the last known hash", FormatUtilities.SanitizeForLog(sourceUser.Name));
                sourceImageHash = existingItem!.SourceImageHash;
                sourceImageSize = existingItem.SourceImageSize;

                // The old tag stays with the old hash, so the next scan reads the image again.
                sourceImageTag = existingItem.SourceValueHash;
            }
        }

        if (localUser.ProfileImage != null && !string.IsNullOrEmpty(localUser.ProfileImage.Path))
        {
            try
            {
                if (File.Exists(localUser.ProfileImage.Path))
                {
                    (localImageHash, localImageSize) = await ImageUtilities.HashFileAsync(localUser.ProfileImage.Path, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "ProfileImage: failed to compute local hash for {User}", FormatUtilities.SanitizeForLog(localUser.Username));
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

        // A ProfileImage row has no value to fingerprint, so this holds the source's image tag the hash was read under.
        item.SourceValueHash = sourceImageTag;
        item.LocalImageHash = localImageHash;
        item.SyncedImageHash = syncedImageHash;
        item.SourceImageSize = sourceImageSize;
        item.LocalImageSize = localImageSize;
        item.SyncedImageSize = existingItem?.SyncedImageSize;
        item.StatusDate = DateTime.UtcNow;
        return item;
    }
}
