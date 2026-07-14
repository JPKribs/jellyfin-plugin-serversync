using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Sync phase for User. Reads Queued rows and applies the per-category
/// change (Policy / Configuration / ProfileImage) directly to local users.
/// On success, mutates the in-memory record so the base's MarkSynced
/// short-circuit fires next refresh; on failure, throws so the base records
/// the reason and transitions to <see cref="Models.Common.SyncStatus.Errored"/>.
/// </summary>
public class SyncMissingUserTask
    : SyncQueueTaskBase<UserSyncItem, (string SourceUserId, string LocalUserId, string PropertyCategory)>
{
    private readonly IUserManager _userManager;
    private readonly IProviderManager _providerManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public SyncMissingUserTask(
        ILogger<SyncMissingUserTask> logger,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        IUserManager userManager,
        IProviderManager providerManager,
        IServerConfigurationManager serverConfigurationManager,
        UserSyncTableManager manager)
        : base(logger, manager, clientFactory, configManager)
    {
        _userManager = userManager;
        _providerManager = providerManager;
        _serverConfigurationManager = serverConfigurationManager;
    }

    /// <inheritdoc />
    public override string Name => "Sync User";

    /// <inheritdoc />
    public override string Key => "ServerSyncMissingUserData";

    /// <inheritdoc />
    public override string Description => "Applies queued user setting changes (policy, configuration, profile image) to local users.";

    /// <inheritdoc />
    public override string Category => "User Sync";

    /// <inheritdoc />
    protected override string ModuleMutexKey => "User";

    /// <inheritdoc />
    protected override bool IsEnabled()
    {
        var config = ConfigManager.Configuration;
        return config.EnableUserSync
            && !string.IsNullOrWhiteSpace(config.SourceServerUrl)
            && !string.IsNullOrWhiteSpace(config.SourceServerApiKey);
    }

    /// <inheritdoc />
    protected override async Task ApplyAsync(UserSyncItem record, CancellationToken cancellationToken)
    {
        var (_, localUser) = ResolveLocalUser(record);

        switch (record.PropertyCategory)
        {
            case UserPropertyCategory.Policy:
                await ApplyPolicyChangesAsync(localUser, record).ConfigureAwait(false);
                break;

            case UserPropertyCategory.Configuration:
                await ApplyConfigurationChangesAsync(localUser, record).ConfigureAwait(false);
                break;

            case UserPropertyCategory.ProfileImage:
                if (Client == null)
                {
                    throw new InvalidOperationException("Source client unavailable for profile-image sync");
                }

                // ApplyProfileImageAsync already updates Local* fields with the
                // hash of the bytes it just wrote so VerifyAfterApplyAsync can
                // compare Local* against Source* in the standard way.
                await ApplyProfileImageAsync(localUser, record, Client, cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException($"Unknown property category: {record.PropertyCategory}");
        }
    }

    /// <inheritdoc />
    protected override Task VerifyAfterApplyAsync(UserSyncItem record, CancellationToken cancellationToken)
    {
        var (localUserId, localUser) = ResolveLocalUser(record);

        switch (record.PropertyCategory)
        {
            case UserPropertyCategory.Policy:
                VerifyPolicyApplied(localUserId, record);
                Logger.LogInformation("Apply Policy verified for {User}", localUser.Username);
                break;

            case UserPropertyCategory.Configuration:
                VerifyConfigurationApplied(localUserId, record);
                Logger.LogInformation("Apply Configuration verified for {User}", localUser.Username);
                break;

            case UserPropertyCategory.ProfileImage:
                VerifyProfileImageApplied(record);
                Logger.LogInformation("Apply ProfileImage verified for {User}", localUser.Username);
                break;

            default:
                throw new InvalidOperationException($"Unknown property category: {record.PropertyCategory}");
        }

        return Task.CompletedTask;
    }

    private (Guid LocalUserId, Jellyfin.Database.Implementations.Entities.User LocalUser) ResolveLocalUser(UserSyncItem record)
    {
        var localUserId = Guid.Parse(record.LocalUserId);
        var localUser = _userManager.GetUserById(localUserId)
            ?? throw new InvalidOperationException($"Local user not found: {record.LocalUserName ?? record.LocalUserId}");
        return (localUserId, localUser);
    }

    private void VerifyPolicyApplied(Guid localUserId, UserSyncItem record)
    {
        var freshUser = _userManager.GetUserById(localUserId)
            ?? throw new InvalidOperationException("user disappeared after policy apply");
        var freshPolicy = _userManager.GetUserDto(freshUser).Policy;
        if (freshPolicy == null)
        {
            throw new InvalidOperationException("policy unavailable after apply (cannot verify)");
        }

        if (string.IsNullOrEmpty(record.MergedValue))
        {
            return;
        }

        var mergedProps = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(record.MergedValue);
        if (mergedProps == null) return;

        var policyType = freshPolicy.GetType();
        var diffs = new List<string>();
        foreach (var kvp in mergedProps)
        {
            var prop = policyType.GetProperty(kvp.Key);
            if (prop == null || !prop.CanRead) continue;

            object? actual = prop.GetValue(freshPolicy);
            object? wanted;
            try
            {
                wanted = JsonSerializer.Deserialize(kvp.Value.GetRawText(), prop.PropertyType);
            }
            catch (JsonException)
            {
                continue;
            }

            if (!ScalarEquals(actual, wanted))
            {
                diffs.Add(kvp.Key);
                if (diffs.Count >= 5) break;
            }
        }

        if (diffs.Count > 0)
        {
            throw new InvalidOperationException($"Policy verification mismatch on: {string.Join(", ", diffs)}");
        }
    }

    private void VerifyConfigurationApplied(Guid localUserId, UserSyncItem record)
    {
        var freshUser = _userManager.GetUserById(localUserId)
            ?? throw new InvalidOperationException("user disappeared after configuration apply");
        var freshConfig = _userManager.GetUserDto(freshUser).Configuration;
        if (freshConfig == null)
        {
            throw new InvalidOperationException("configuration unavailable after apply (cannot verify)");
        }

        if (string.IsNullOrEmpty(record.MergedValue))
        {
            return;
        }

        var mergedProps = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(record.MergedValue);
        if (mergedProps == null) return;

        var configType = freshConfig.GetType();
        var diffs = new List<string>();
        foreach (var kvp in mergedProps)
        {
            var prop = configType.GetProperty(kvp.Key);
            if (prop == null || !prop.CanRead) continue;

            object? actual = prop.GetValue(freshConfig);
            object? wanted;
            try
            {
                wanted = JsonSerializer.Deserialize(kvp.Value.GetRawText(), prop.PropertyType);
            }
            catch (JsonException)
            {
                continue;
            }

            if (!ScalarEquals(actual, wanted))
            {
                diffs.Add(kvp.Key);
                if (diffs.Count >= 5) break;
            }
        }

        if (diffs.Count > 0)
        {
            throw new InvalidOperationException($"Configuration verification mismatch on: {string.Join(", ", diffs)}");
        }
    }

    private static void VerifyProfileImageApplied(UserSyncItem record)
    {
        // ApplyProfileImageAsync already wrote the downloaded hash into
        // LocalImageHash. Verifying = confirming Local matches Source.
        var sourceHasNoImage = string.IsNullOrEmpty(record.SourceImageHash)
            && (!record.SourceImageSize.HasValue || record.SourceImageSize <= 0);
        if (sourceHasNoImage)
        {
            // Apply is "remove local image" — verify local has no image.
            var localHasImage = !string.IsNullOrEmpty(record.LocalImageHash)
                || (record.LocalImageSize.HasValue && record.LocalImageSize > 0);
            if (localHasImage)
            {
                throw new InvalidOperationException("ProfileImage: local still has an image after apply intended to clear it");
            }

            return;
        }

        if (!string.IsNullOrEmpty(record.SourceImageHash)
            && !string.Equals(record.SourceImageHash, record.LocalImageHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"ProfileImage: hash mismatch after apply (source {record.SourceImageHash}, local {record.LocalImageHash})");
        }

        if (string.IsNullOrEmpty(record.SourceImageHash)
            && record.SourceImageSize.HasValue
            && record.LocalImageSize != record.SourceImageSize)
        {
            throw new InvalidOperationException(
                $"ProfileImage: size mismatch after apply (source {record.SourceImageSize}, local {record.LocalImageSize})");
        }
    }

    private static bool ScalarEquals(object? a, object? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        // Reflection comparison — for collections, fall back to JSON.
        if (a is System.Collections.IEnumerable ea && b is System.Collections.IEnumerable eb
            && a is not string && b is not string)
        {
            return JsonSerializer.Serialize(ea) == JsonSerializer.Serialize(eb);
        }

        return a.Equals(b);
    }

    /// <inheritdoc />
    protected override void RecordRunCompleted(Jellyfin.Plugin.ServerSync.Configuration.PluginConfiguration config, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.LastUserSyncTime = utcNow;
    }

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = MediaBrowser.Model.Tasks.TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(24).Ticks
        }
    };

    // ===================================================================
    // Apply logic — per-category mutations on the local user. Each method
    // updates the record's in-memory fields on success so the base's upsert
    // captures the new state and the next Refresh sees no diff.
    // ===================================================================

    private Task ApplyPolicyChangesAsync(User localUser, UserSyncItem item)
        => ApplyJsonPropertyChangesAsync(
            localUser,
            item,
            categoryLabel: "Policy",
            getTarget: dto => dto.Policy,
            persistAsync: (id, policy) => _userManager.UpdatePolicyAsync(id, policy));

    private Task ApplyConfigurationChangesAsync(User localUser, UserSyncItem item)
        => ApplyJsonPropertyChangesAsync(
            localUser,
            item,
            categoryLabel: "Configuration",
            getTarget: dto => dto.Configuration,
            persistAsync: (id, config) => _userManager.UpdateConfigurationAsync(id, config));

    /// <summary>
    /// Shared apply path for Policy and Configuration: deserialize the merged
    /// JSON blob, reflect each key onto the typed target object, log unknown
    /// fields, and call the category-specific persist delegate. Both
    /// categories had near-identical bodies — this collapses them so a fix
    /// to the apply loop lands in one place.
    /// </summary>
    private async Task ApplyJsonPropertyChangesAsync<T>(
        User localUser,
        UserSyncItem item,
        string categoryLabel,
        Func<MediaBrowser.Model.Dto.UserDto, T?> getTarget,
        Func<Guid, T, Task> persistAsync)
        where T : class
    {
        if (string.IsNullOrEmpty(item.MergedValue))
        {
            item.LocalValue = item.MergedValue;
            return;
        }

        var localUserDto = _userManager.GetUserDto(localUser);
        var target = getTarget(localUserDto)
            ?? throw new InvalidOperationException($"Could not retrieve current {categoryLabel.ToLowerInvariant()} for user {localUser.Username}");

        var mergedProps = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.MergedValue);
        if (mergedProps == null)
        {
            item.LocalValue = item.MergedValue;
            return;
        }

        var targetType = target.GetType();
        var modified = false;
        var appliedProperties = new List<string>();
        var skippedProperties = new List<string>();

        foreach (var kvp in mergedProps)
        {
            try
            {
                var property = targetType.GetProperty(kvp.Key);
                if (property == null || !property.CanWrite)
                {
                    skippedProperties.Add(kvp.Key);
                    continue;
                }

                var value = JsonSerializer.Deserialize(kvp.Value.GetRawText(), property.PropertyType);
                property.SetValue(target, value);
                modified = true;
                appliedProperties.Add(kvp.Key);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "{Category}: failed to apply property {Property} for {User}", categoryLabel, kvp.Key, localUser.Username);
            }
        }

        if (skippedProperties.Count > 0)
        {
            // Source server's schema has fields that don't exist (or aren't
            // settable) on local — cross-Jellyfin-version drift. Surface so
            // the operator can see what didn't make it across.
            Logger.LogDebug("{Category}: skipped {Count} unknown/unsettable propert(ies) for {User}: {Properties}",
                categoryLabel, skippedProperties.Count, localUser.Username, string.Join(", ", skippedProperties));
        }

        if (modified)
        {
            Logger.LogDebug("{Category}: applying {Count} changes for {User}: {Properties}",
                categoryLabel, appliedProperties.Count, localUser.Username, string.Join(", ", appliedProperties));
            await persistAsync(localUser.Id, target).ConfigureAwait(false);
        }

        item.LocalValue = item.MergedValue;
    }

    private async Task ApplyProfileImageAsync(
        User localUser,
        UserSyncItem item,
        SourceServerClient sourceClient,
        CancellationToken cancellationToken)
    {
        // Already in sync (hash match) — populate synced fields and return.
        if (!string.IsNullOrEmpty(item.SourceImageHash)
            && string.Equals(item.SourceImageHash, item.LocalImageHash, StringComparison.OrdinalIgnoreCase))
        {
            item.SyncedImageHash = item.SourceImageHash;
            item.SyncedImageSize = item.SourceImageSize;
            return;
        }

        // Fallback size match.
        if (string.IsNullOrEmpty(item.SourceImageHash)
            && item.SourceImageSize.HasValue
            && item.LocalImageSize.HasValue
            && item.SourceImageSize == item.LocalImageSize)
        {
            item.SyncedImageSize = item.SourceImageSize;
            return;
        }

        var sourceHasNoImage = string.IsNullOrEmpty(item.SourceImageHash)
            && (!item.SourceImageSize.HasValue || item.SourceImageSize <= 0);
        if (sourceHasNoImage)
        {
            if (localUser.ProfileImage != null)
            {
                Logger.LogDebug("ProfileImage: removing local image (source has none) for {User}", localUser.Username);
                await _userManager.ClearProfileImageAsync(localUser).ConfigureAwait(false);
            }

            item.LocalImageHash = null;
            item.LocalImageSize = 0;
            item.SyncedImageHash = null;
            item.SyncedImageSize = 0;
            item.LocalValue = item.SourceValue;
            return;
        }

        var sourceUserId = Guid.Parse(item.SourceUserId);

        using var imageStream = await sourceClient.GetUserImageAsync(sourceUserId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Failed to download profile image for {item.SourceUserName ?? item.SourceUserId}");

        var userDataPath = Path.Combine(
            _serverConfigurationManager.ApplicationPaths.UserConfigurationDirectoryPath,
            localUser.Username);
        var profilePath = Path.Combine(userDataPath, "profile.jpg");
        // Both the download buffer and the staging file live inside the user's
        // profile directory (a Jellyfin folder), so nothing is written outside
        // the data tree and File.Move stays intra-filesystem (atomic). Distinct
        // sentinel names keep the existing profile.jpg working until the new
        // image is verified and promoted.
        var stagingPath = Path.Combine(userDataPath, "profile.jpg.serversync.staging");
        var tempPath = Path.Combine(userDataPath, "profile.jpg.serversync.download");
        Directory.CreateDirectory(userDataPath);

        try
        {
            // Phase 1: download and hash. tempPath is a fresh temp file —
            // failures here don't touch local state.
            using (var fileStream = File.Create(tempPath))
            {
                await imageStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            string downloadedHash;
            using (var verifyStream = File.OpenRead(tempPath))
            {
                downloadedHash = HashUtilities.ComputeSha256Hash(verifyStream);
            }

            var downloadedSize = new FileInfo(tempPath).Length;
            if (downloadedSize <= 0)
            {
                throw new InvalidOperationException(
                    $"Downloaded profile image for {item.SourceUserName ?? item.SourceUserId} was empty (0 bytes)");
            }

            // Phase 2: durably stage the new image inside the user's profile
            // directory under a sentinel name. If anything below fails after
            // the stage, the staging file is still present for the next sync
            // run (or manual recovery) and the user's existing profile.jpg
            // remains untouched.
            File.Copy(tempPath, stagingPath, overwrite: true);

            // Phase 3: clear the existing image (DB row + file). Safe now
            // because the new image is already staged on disk.
            if (localUser.ProfileImage != null)
            {
                await _userManager.ClearProfileImageAsync(localUser).ConfigureAwait(false);
                localUser = _userManager.GetUserById(localUser.Id)
                    ?? throw new InvalidOperationException("User disappeared after clearing profile image");
            }

            // Phase 4: atomically promote staging → profile.jpg. Single FS
            // operation — overwrite is safe because Phase 3 just removed
            // the old profile.jpg.
            File.Move(stagingPath, profilePath, overwrite: true);

            // Phase 5: update the DB pointer. Even if this throws, the file
            // is in place; the next refresh will see source == local on the
            // file and reconcile the DB row.
            localUser.ProfileImage = new ImageInfo(profilePath);
            await _userManager.UpdateUserAsync(localUser).ConfigureAwait(false);

            item.LocalImageHash = downloadedHash;
            item.LocalImageSize = downloadedSize;
            item.SyncedImageHash = downloadedHash;
            item.SyncedImageSize = downloadedSize;
            item.LocalValue = item.SourceValue;

            Logger.LogInformation("ProfileImage: updated for {User} (hash: {Hash}, size: {Size})",
                localUser.Username, downloadedHash, downloadedSize);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup of the in-profile download buffer.
            }

            // Clean up staging only if it survived past the move (i.e. the
            // move never happened). If the move succeeded, staging no
            // longer exists at that path.
            try
            {
                if (File.Exists(stagingPath))
                {
                    File.Delete(stagingPath);
                }
            }
            catch (IOException)
            {
                // Same — best-effort cleanup.
            }
        }
    }
}
