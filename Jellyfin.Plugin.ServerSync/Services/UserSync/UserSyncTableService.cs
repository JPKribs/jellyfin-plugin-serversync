using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.UserSync;

/// <summary>
/// Per-record build helpers for User Sync. Builds one
/// <see cref="UserSyncItem"/> for one (mapping, category) tuple, preserving
/// LastSyncTime and Ignored status from any existing record. No DB writes:
/// the caller (the Refresh task base) upserts.
/// </summary>
[PluginService(ServiceLifetime.Transient)]
public class UserSyncTableService
{
    private readonly ILogger<UserSyncTableService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserSyncTableService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public UserSyncTableService(ILogger<UserSyncTableService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Builds a <see cref="UserSyncItem"/> for one (mapping, category) tuple.
    /// Returns null for an unknown category. The category-specific helpers
    /// are private, callers go through this single entry point so adding a
    /// category is a switch-arm addition rather than a new public method.
    /// </summary>
    /// <param name="mapping">The user mapping.</param>
    /// <param name="category">The property category: Policy, Configuration, or ProfileImage.</param>
    /// <param name="sourceUser">The user as the source returned it.</param>
    /// <param name="localUserDto">The local user as Jellyfin's API shows it.</param>
    /// <param name="localUser">The local user's entity.</param>
    /// <param name="sourceClient">The client for the source server.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="existingItem">The stored row, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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
        ArgumentNullException.ThrowIfNull(sourceUser);
        ArgumentNullException.ThrowIfNull(sourceClient);
        ArgumentNullException.ThrowIfNull(config);
        libraryMappings ??= config.GetEnabledLibraryMappings();

        return category switch
        {
            UserPropertyCategory.Policy => BuildPolicyItem(mapping, sourceUser, localUserDto, libraryMappings, existingItem, bothWays),
            UserPropertyCategory.Configuration => BuildConfigurationItem(mapping, sourceUser, localUserDto, existingItem, bothWays),
            UserPropertyCategory.ProfileImage => await BuildProfileImageItemAsync(mapping, sourceUser, localUser, sourceClient, existingItem, cancellationToken).ConfigureAwait(false),
            _ => null
        };
    }

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
        // UpdateMergedValue stores MergedValue (the value to apply) on the
        // backing SyncableValue and recomputes the source-hash via the
        // comparator. SyncedHash matches that format after a successful sync,
        // so the fast-path skip on the next Refresh stays consistent.
        item.UpdateMergedValue(mergedPolicy);
        item.KeepAgreedValue = keep;
        item.StatusDate = DateTime.UtcNow;
        return item;
    }

    /// <summary>
    /// Decides what one category of a user's settings becomes between this server and a server it
    /// syncs with, from which of the two changed it since they last agreed. Only the other server
    /// changed it: its value applies. Only this server changed it: this server's value stays, and the
    /// other server takes it on its own scan. Both changed it: <paramref name="combine"/> decides.
    /// Before the two have ever agreed, the other server's value applies.
    /// </summary>
    /// <param name="seen">The stored row, as the last scan left it, or null.</param>
    /// <param name="theirs">The other server's value in this server's terms.</param>
    /// <param name="mine">This server's value now.</param>
    /// <param name="combine">Combines the two when both changed.</param>
    /// <returns>The value to apply, and whether it is this server's own value being kept.</returns>
    public static (string? Value, bool Keep) SettleBothWays(UserSyncItem? seen, string? theirs, string? mine, Func<string?, string?> combine)
    {
        ArgumentNullException.ThrowIfNull(combine);
        var comparator = new Models.Common.Comparators.JsonBlobComparator();
        if (seen?.SyncedValueHash is not { Length: > 0 } agreed || comparator.Equals(theirs, mine))
        {
            return (theirs, false);
        }

        // The value to apply is built the same way from the same settings on every scan, so the
        // fingerprint taken when the two agreed still matches until the other server changes them.
        var theyChanged = !string.Equals(comparator.ComputeHash(theirs), agreed, StringComparison.Ordinal);
        if (!theyChanged)
        {
            return (mine, true);
        }

        // This server changed it too when the last scan already kept a value of its own, or when
        // its value has moved since that scan. A kept value is a settled row that holds something
        // other than what the two agreed on. A change still waiting to be applied is neither.
        var keptBefore = seen.Status == Models.Common.SyncStatus.Synced
            && !string.Equals(comparator.ComputeHash(seen.MergedValue), agreed, StringComparison.Ordinal);
        var iChanged = keptBefore || !comparator.Equals(seen.LocalValue, mine);
        return (iChanged ? combine(theirs) : theirs, false);
    }

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
            // Preferences have no more and no less, so when both servers changed them the other server's stand.
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
            try
            {
                (sourceImageHash, sourceImageSize) = await sourceClient.GetUserImageHashAndSizeAsync(sourceUserId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (!string.IsNullOrEmpty(existingItem?.SourceImageHash))
            {
                // The source says the user has an image but it could not be read. A null hash here
                // would read as "the source removed its image" and the sync would clear the local
                // one, so keep what the last good read recorded and let the next refresh try again.
                _logger.LogWarning(ex, "ProfileImage: could not read the source image for {User}, keeping the last known hash", sourceUser.Name);
                sourceImageHash = existingItem!.SourceImageHash;
                sourceImageSize = existingItem.SourceImageSize;
            }

            // With no earlier hash there is nothing to keep, so the exception fails this row and the
            // refresh skips its prune for the run.
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
