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
public class SyncMissingUserDataTask
    : SyncQueueTaskBase<UserSyncItem, (string SourceUserId, string LocalUserId, string PropertyCategory)>
{
    private readonly IUserManager _userManager;
    private readonly IProviderManager _providerManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public SyncMissingUserDataTask(
        ILogger<SyncMissingUserDataTask> logger,
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
    public override string Name => "Sync User Data";

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
        var localUserId = Guid.Parse(record.LocalUserId);
        var localUser = _userManager.GetUserById(localUserId)
            ?? throw new InvalidOperationException($"Local user not found: {record.LocalUserName ?? record.LocalUserId}");

        switch (record.PropertyCategory)
        {
            case UserPropertyCategory.Policy:
                await ApplyPolicyChangesAsync(localUser, record).ConfigureAwait(false);
                VerifyPolicyApplied(localUserId, record);
                Logger.LogInformation("Apply Policy verified for {User}", localUser.Username);
                break;

            case UserPropertyCategory.Configuration:
                await ApplyConfigurationChangesAsync(localUser, record).ConfigureAwait(false);
                VerifyConfigurationApplied(localUserId, record);
                Logger.LogInformation("Apply Configuration verified for {User}", localUser.Username);
                break;

            case UserPropertyCategory.ProfileImage:
                if (Client == null)
                {
                    throw new InvalidOperationException("Source client unavailable for profile-image sync");
                }

                await ApplyProfileImageAsync(localUser, record, Client, cancellationToken).ConfigureAwait(false);
                // ApplyProfileImageAsync already updates Local* fields with the
                // hash of the bytes it just wrote; verifying is checking they
                // now match Source*.
                VerifyProfileImageApplied(record);
                Logger.LogInformation("Apply ProfileImage verified for {User}", localUser.Username);
                break;

            default:
                throw new InvalidOperationException($"Unknown property category: {record.PropertyCategory}");
        }
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
    protected override Task FinalizeAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ConfigManager.Configuration.LastUserSyncTime = DateTime.UtcNow;
        ConfigManager.SaveConfiguration();
        return Task.CompletedTask;
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

    private async Task ApplyPolicyChangesAsync(User localUser, UserSyncItem item)
    {
        if (string.IsNullOrEmpty(item.MergedValue))
        {
            item.LocalValue = item.MergedValue;
            return;
        }

        var localUserDto = _userManager.GetUserDto(localUser);
        var localPolicy = localUserDto.Policy
            ?? throw new InvalidOperationException($"Could not retrieve current policy for user {localUser.Username}");

        var mergedProps = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.MergedValue);
        if (mergedProps == null)
        {
            item.LocalValue = item.MergedValue;
            return;
        }

        var policyType = localPolicy.GetType();
        var modified = false;
        var appliedProperties = new List<string>();

        foreach (var kvp in mergedProps)
        {
            try
            {
                var property = policyType.GetProperty(kvp.Key);
                if (property == null || !property.CanWrite)
                {
                    continue;
                }

                var value = JsonSerializer.Deserialize(kvp.Value.GetRawText(), property.PropertyType);
                property.SetValue(localPolicy, value);
                modified = true;
                appliedProperties.Add(kvp.Key);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Policy: failed to apply property {Property} for {User}", kvp.Key, localUser.Username);
            }
        }

        if (modified)
        {
            Logger.LogDebug("Policy: applying {Count} changes for {User}: {Properties}",
                appliedProperties.Count, localUser.Username, string.Join(", ", appliedProperties));
            await _userManager.UpdatePolicyAsync(localUser.Id, localPolicy).ConfigureAwait(false);
        }

        item.LocalValue = item.MergedValue;
    }

    private async Task ApplyConfigurationChangesAsync(User localUser, UserSyncItem item)
    {
        if (string.IsNullOrEmpty(item.MergedValue))
        {
            item.LocalValue = item.MergedValue;
            return;
        }

        var localUserDto = _userManager.GetUserDto(localUser);
        var localConfig = localUserDto.Configuration
            ?? throw new InvalidOperationException($"Could not retrieve current configuration for user {localUser.Username}");

        var mergedProps = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.MergedValue);
        if (mergedProps == null)
        {
            item.LocalValue = item.MergedValue;
            return;
        }

        var configType = localConfig.GetType();
        var modified = false;
        var appliedProperties = new List<string>();

        foreach (var kvp in mergedProps)
        {
            try
            {
                var property = configType.GetProperty(kvp.Key);
                if (property == null || !property.CanWrite)
                {
                    continue;
                }

                var value = JsonSerializer.Deserialize(kvp.Value.GetRawText(), property.PropertyType);
                property.SetValue(localConfig, value);
                modified = true;
                appliedProperties.Add(kvp.Key);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Configuration: failed to apply property {Property} for {User}", kvp.Key, localUser.Username);
            }
        }

        if (modified)
        {
            Logger.LogDebug("Configuration: applying {Count} changes for {User}: {Properties}",
                appliedProperties.Count, localUser.Username, string.Join(", ", appliedProperties));
            await _userManager.UpdateConfigurationAsync(localUser.Id, localConfig).ConfigureAwait(false);
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

        // Avoid Path.GetTempFileName: it creates a 0-byte file we'd then have
        // to rename to *.jpg, leaking the original.
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            using (var fileStream = File.Create(tempPath))
            {
                await imageStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            string downloadedHash;
            using (var verifyStream = File.OpenRead(tempPath))
            {
                downloadedHash = HashUtilities.ComputeSha256Hash(verifyStream);
            }

            // Clear-and-set is how Jellyfin's API operates internally.
            if (localUser.ProfileImage != null)
            {
                await _userManager.ClearProfileImageAsync(localUser).ConfigureAwait(false);
                localUser = _userManager.GetUserById(localUser.Id)
                    ?? throw new InvalidOperationException("User disappeared after clearing profile image");
            }

            var userDataPath = Path.Combine(
                _serverConfigurationManager.ApplicationPaths.UserConfigurationDirectoryPath,
                localUser.Username);
            Directory.CreateDirectory(userDataPath);
            var profilePath = Path.Combine(userDataPath, "profile.jpg");

            using (var profileStream = File.OpenRead(tempPath))
            {
                await _providerManager.SaveImage(profileStream, "image/jpeg", profilePath).ConfigureAwait(false);
            }

            localUser.ProfileImage = new ImageInfo(profilePath);
            await _userManager.UpdateUserAsync(localUser).ConfigureAwait(false);

            var downloadedSize = new FileInfo(tempPath).Length;
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
                // Best-effort cleanup; OS will clean temp eventually.
            }
        }
    }
}
