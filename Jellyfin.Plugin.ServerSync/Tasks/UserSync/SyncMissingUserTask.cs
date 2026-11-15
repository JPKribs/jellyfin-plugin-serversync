using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Services.UserSync;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.UserSync;

/// <summary>
/// Applies queued Users rows, one category each, straight to the local user.
/// A failure throws, so the base stores the reason and makes the row <see cref="Models.Common.SyncStatus.Errored"/>.
/// </summary>
public class SyncMissingUserTask
    : SyncQueueTaskBase<UserSyncItem, (string SourceUserId, string LocalUserId, string PropertyCategory)>
{
    private readonly IUserManager _userManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly AppliedVersionRecorder? _applied;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncMissingUserTask"/> class.
    /// </summary>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="clientFactory">Builds the client for each source.</param>
    /// <param name="userManager">Reads Jellyfin's users.</param>
    /// <param name="providerManager">Refreshes an item's metadata and images.</param>
    /// <param name="serverConfigurationManager">Reads and saves Jellyfin's server settings.</param>
    /// <param name="manager">Reads and writes the sync table.</param>
    /// <param name="applied">Records the stamp each applied value carries, or null when stamps are not kept.</param>
    public SyncMissingUserTask(
        ILogger<SyncMissingUserTask> logger,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        IUserManager userManager,
        IProviderManager providerManager,
        IServerConfigurationManager serverConfigurationManager,
        UserSyncTableManager manager,
        AppliedVersionRecorder? applied = null)
        : base(logger, manager, clientFactory, configManager)
    {
        _userManager = userManager;
        _serverConfigurationManager = serverConfigurationManager;
        _applied = applied;
    }

    /// <inheritdoc />
    protected override IDisposable? EnterApplyGuard(UserSyncItem record)
        => _applied?.Enter(Models.Queue.HintKind.Users, Guid.TryParse(record?.LocalUserId, out var id) ? HintProtocol.UsersKey(id) : string.Empty);

    /// <summary>
    /// Records the stamp the applied settings carry, once for each user.
    /// </summary>
    /// <inheritdoc />
    protected override async Task AfterApplySucceededAsync(UserSyncItem record, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(record?.LocalUserId, out var localId) || !Guid.TryParse(record.SourceUserId, out var sourceId))
        {
            return;
        }

        if (_applied is not null)
        {
            await _applied.RecordAsync(Models.Queue.HintKind.Users, HintProtocol.UsersKey(localId), HintProtocol.UsersKey(sourceId), SourceFor(record), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override string Name => "Sync Users";

    /// <inheritdoc />
    public override string Key => "ServerSyncMissingUserData";

    /// <inheritdoc />
    public override string Description => "Applies queued user setting changes (policy, configuration, profile image) to local users.";

    /// <inheritdoc />
    public override string Category => "User Sync";

    /// <summary>
    /// Stores the applied settings as what this server holds, so the next scan can tell a change made here by comparing against it.
    /// </summary>
    /// <inheritdoc />
    protected override void OnApplySucceeded(UserSyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.PropertyCategory != UserPropertyCategory.ProfileImage)
        {
            record.LocalValue = record.MergedValue;
        }

        base.OnApplySucceeded(record);
    }

    /// <inheritdoc />
    protected override string ModuleMutexKey => "User";

    /// <inheritdoc />
    protected override bool IsEnabled()
    {
        var config = ConfigManager.Configuration;
        return config.EnableUserSync && config.GetPullServers().Count > 0;
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
                await ApplyProfileImageAsync(localUser, record, RequireSource(record).Client, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Finds the row's local user, or throws naming the user.</summary>
    private (Guid LocalUserId, User LocalUser) ResolveLocalUser(UserSyncItem record)
    {
        var localUserId = Guid.Parse(record.LocalUserId);
        var localUser = _userManager.GetUserById(localUserId)
            ?? throw new InvalidOperationException($"Local user not found: {record.LocalUserName ?? record.LocalUserId}");
        return (localUserId, localUser);
    }

    /// <summary>Reads the policy back and throws naming up to five properties that did not land.</summary>
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

    /// <summary>Reads the configuration back and throws naming up to five properties that did not land.</summary>
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

    /// <summary>Confirms the row's local image matches the source's, or is gone when the source has none.</summary>
    private static void VerifyProfileImageApplied(UserSyncItem record)
    {
        var sourceHasNoImage = string.IsNullOrEmpty(record.SourceImageHash)
            && (!record.SourceImageSize.HasValue || record.SourceImageSize <= 0);
        if (sourceHasNoImage)
        {
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

    /// <summary>Compares two property values, and collections by their JSON.</summary>
    private static bool ScalarEquals(object? a, object? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a is System.Collections.IEnumerable ea && b is System.Collections.IEnumerable eb
            && a is not string && b is not string)
        {
            return JsonSerializer.Serialize(ea) == JsonSerializer.Serialize(eb);
        }

        return a.Equals(b);
    }

    /// <inheritdoc />
    protected override void RecordRunCompleted(PluginConfiguration config, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.LastUserSyncTime = utcNow;
    }

    /// <summary>Applies the merged policy to the local user.</summary>
    private Task ApplyPolicyChangesAsync(User localUser, UserSyncItem item)
        => ApplyJsonPropertyChangesAsync(
            localUser,
            item,
            categoryLabel: "Policy",
            maySync: UserSyncMergeService.ShouldSyncPolicyProperty,
            getTarget: dto => dto.Policy,
            persistAsync: (id, policy) => _userManager.UpdatePolicyAsync(id, policy));

    /// <summary>Applies the merged configuration to the local user.</summary>
    private Task ApplyConfigurationChangesAsync(User localUser, UserSyncItem item)
        => ApplyJsonPropertyChangesAsync(
            localUser,
            item,
            categoryLabel: "Configuration",
            maySync: UserSyncMergeService.ShouldSyncConfigurationProperty,
            getTarget: dto => dto.Configuration,
            persistAsync: (id, config) => _userManager.UpdateConfigurationAsync(id, config));

    /// <summary>
    /// Applies Policy or Configuration: reads the merged JSON, sets each property that may be copied on the typed target, and saves it through the category's own save.
    /// </summary>
    private async Task ApplyJsonPropertyChangesAsync<T>(
        User localUser,
        UserSyncItem item,
        string categoryLabel,
        Func<System.Reflection.PropertyInfo, bool> maySync,
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
                // The merged value comes from the table, so the copy rule is applied again.
                if (property == null || !property.CanWrite || !maySync(property))
                {
                    skippedProperties.Add(kvp.Key);
                    continue;
                }

                var value = JsonSerializer.Deserialize(kvp.Value.GetRawText(), property.PropertyType);
                property.SetValue(target, value);
                modified = true;
                appliedProperties.Add(kvp.Key);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException or InvalidOperationException or System.Reflection.TargetInvocationException or System.Reflection.AmbiguousMatchException)
            {
                Logger.LogWarning(ex, "{Category}: failed to apply property {Property} for {User}", categoryLabel, FormatUtilities.SanitizeForLog(kvp.Key), localUser.Username);
            }
        }

        if (skippedProperties.Count > 0)
        {
            // Jellyfin versions differ in their settings, so the skipped ones are logged.
            Logger.LogDebug("{Category}: skipped {Count} unknown/unsettable propert(ies) for {User}: {Properties}",
                categoryLabel, skippedProperties.Count, localUser.Username, FormatUtilities.SanitizeForLog(string.Join(", ", skippedProperties), 300));
        }

        if (modified)
        {
            Logger.LogDebug("{Category}: applying {Count} changes for {User}: {Properties}",
                categoryLabel, appliedProperties.Count, localUser.Username, FormatUtilities.SanitizeForLog(string.Join(", ", appliedProperties), 300));
            await persistAsync(localUser.Id, target).ConfigureAwait(false);
        }

        item.LocalValue = item.MergedValue;
    }

    /// <summary>Replaces the local profile image with the source's, staging the download beside the old image so it stays in place until the new one is ready.</summary>
    private async Task ApplyProfileImageAsync(
        User localUser,
        UserSyncItem item,
        SourceServerClient sourceClient,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(item.SourceImageHash)
            && string.Equals(item.SourceImageHash, item.LocalImageHash, StringComparison.OrdinalIgnoreCase))
        {
            item.SyncedImageHash = item.SourceImageHash;
            item.SyncedImageSize = item.SourceImageSize;
            return;
        }

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
        // Both files sit in the profile folder, so the move stays on one filesystem.
        var stagingPath = Path.Combine(userDataPath, "profile.jpg.serversync.staging");
        var tempPath = Path.Combine(userDataPath, "profile.jpg.serversync.download");
        Directory.CreateDirectory(userDataPath);
        var promoted = false;

        try
        {
            using (var fileStream = File.Create(tempPath))
            {
                await StreamUtilities.CopyWithIdleTimeoutAsync(new CappedReadStream(imageStream, StreamCaps.MaxSyncedImageBytes), fileStream, StreamUtilities.DefaultIdleTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            string downloadedHash;
            using (var verifyStream = File.OpenRead(tempPath))
            {
                downloadedHash = (await HashUtilities.ComputeSha256HashAsync(verifyStream, cancellationToken).ConfigureAwait(false)).Hash;
            }

            var downloadedSize = new FileInfo(tempPath).Length;
            if (downloadedSize <= 0)
            {
                throw new InvalidOperationException(
                    $"Downloaded profile image for {item.SourceUserName ?? item.SourceUserId} was empty (0 bytes)");
            }

            // Staged first, so a later failure leaves the old image untouched.
            File.Copy(tempPath, stagingPath, overwrite: true);

            // Safe to clear, since the new image is already staged.
            if (localUser.ProfileImage != null)
            {
                await _userManager.ClearProfileImageAsync(localUser).ConfigureAwait(false);
                localUser = _userManager.GetUserById(localUser.Id)
                    ?? throw new InvalidOperationException("User disappeared after clearing profile image");
            }

            File.Move(stagingPath, profilePath, overwrite: true);
            promoted = true;

            // The file is in place even if this throws, and the next scan reconciles.
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
                // Cleanup is best effort.
            }

            // Kept when the move never happened, so the new image can be recovered.
            if (promoted)
            {
                try
                {
                    if (File.Exists(stagingPath))
                    {
                        File.Delete(stagingPath);
                    }
                }
                catch (IOException)
                {
                    // Cleanup is best effort.
                }
            }
            else if (File.Exists(stagingPath))
            {
                Logger.LogWarning("ProfileImage: update for {User} did not complete, the downloaded image is kept at {Path} for recovery", localUser.Username, stagingPath);
            }
        }
    }
}
