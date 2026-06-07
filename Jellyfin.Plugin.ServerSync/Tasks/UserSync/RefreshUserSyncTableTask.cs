using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using TaskTriggerInfo = MediaBrowser.Model.Tasks.TaskTriggerInfo;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// One unit of refresh work for User Sync — a single (mapping, category)
/// pairing carrying the source-fetched data needed to build that record.
/// Pre-fetching at the mapping level (rather than per-category) keeps the
/// network calls down to one per source user.
/// </summary>
public sealed class UserCategoryWork
{
    public required UserMapping Mapping { get; init; }

    public required string Category { get; init; }

    public required Jellyfin.Sdk.Generated.Models.UserDto SourceUser { get; init; }

    public required MediaBrowser.Model.Dto.UserDto LocalUserDto { get; init; }

    public required Jellyfin.Database.Implementations.Entities.User LocalUser { get; init; }
}

/// <summary>
/// Refresh phase for User Sync. Expands each enabled user mapping into one
/// work item per enabled category (Policy / Configuration / ProfileImage),
/// then builds one <see cref="UserSyncItem"/> per work item. The base handles
/// upsert, status decision, and pruning.
/// </summary>
public class RefreshUserSyncTableTask
    : RefreshSyncTaskBase<UserSyncItem, UserCategoryWork, (string SourceUserId, string LocalUserId, string PropertyCategory)>
{
    private readonly IUserManager _userManager;
    private readonly UserSyncTableService _builder;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public RefreshUserSyncTableTask(
        ILogger<RefreshUserSyncTableTask> logger,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        IUserManager userManager,
        UserSyncTableService builder,
        UserSyncTableManager manager)
        : base(logger, manager, clientFactory, configManager)
    {
        _userManager = userManager;
        _builder = builder;
    }

    /// <inheritdoc />
    public override string Name => "Refresh User Sync Table";

    /// <inheritdoc />
    public override string Key => "ServerSyncRefreshUserTable";

    /// <inheritdoc />
    public override string Description => "Scans source and local servers for user setting differences and updates the user sync table.";

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
            && !string.IsNullOrWhiteSpace(config.SourceServerApiKey)
            && config.GetEnabledUserMappings().Count > 0;
    }

    /// <inheritdoc />
    protected override async Task<IList<UserCategoryWork>> GetListAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (Client == null)
        {
            return Array.Empty<UserCategoryWork>();
        }

        var config = ConfigManager.Configuration;
        var enabledMappings = config.GetEnabledUserMappings();
        var work = new List<UserCategoryWork>();
        var processedMappings = 0;
        var totalMappings = Math.Max(enabledMappings.Count, 1);
        progress.Report(0);

        foreach (var mapping in enabledMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var sourceUserId = Guid.Parse(mapping.SourceUserId);
                var localUserId = Guid.Parse(mapping.LocalUserId);

                var sourceUser = await Client.GetUserAsync(sourceUserId, cancellationToken).ConfigureAwait(false);
                if (sourceUser == null)
                {
                    // Source user fetch returned null (transient API failure or
                    // genuinely missing user — we can't tell). Skip pruning this
                    // run so a network blip doesn't delete the mapping's rows.
                    MarkSourceUnavailable($"source server unavailable — source user '{mapping.SourceUserName}' not returned");
                    Logger.LogWarning("Source user {Id} not found; skipping mapping {Source} -> {Local}",
                        mapping.SourceUserId, mapping.SourceUserName, mapping.LocalUserName);
                    continue;
                }

                var localUser = _userManager.GetUserById(localUserId);
                if (localUser == null)
                {
                    // Local target gone: we can't enumerate this mapping, so skip
                    // pruning this run rather than treat its rows as deleted.
                    MarkSourceUnavailable($"local user '{mapping.LocalUserName}' not found; mapping rows cannot be re-confirmed");
                    Logger.LogWarning("Local user {Id} not found; skipping mapping {Source} -> {Local}",
                        mapping.LocalUserId, mapping.SourceUserName, mapping.LocalUserName);
                    continue;
                }

                var localUserDto = _userManager.GetUserDto(localUser);

                if (config.UserSyncPolicy)
                {
                    work.Add(new UserCategoryWork
                    {
                        Mapping = mapping,
                        Category = UserPropertyCategory.Policy,
                        SourceUser = sourceUser,
                        LocalUserDto = localUserDto,
                        LocalUser = localUser
                    });
                }

                if (config.UserSyncConfiguration)
                {
                    work.Add(new UserCategoryWork
                    {
                        Mapping = mapping,
                        Category = UserPropertyCategory.Configuration,
                        SourceUser = sourceUser,
                        LocalUserDto = localUserDto,
                        LocalUser = localUser
                    });
                }

                if (config.UserSyncProfileImage)
                {
                    work.Add(new UserCategoryWork
                    {
                        Mapping = mapping,
                        Category = UserPropertyCategory.ProfileImage,
                        SourceUser = sourceUser,
                        LocalUserDto = localUserDto,
                        LocalUser = localUser
                    });
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Guid.Parse or GetUserAsync threw. Skip pruning this run so the
                // mapping's rows survive until the next run confirms source state.
                MarkSourceUnavailable($"source server unavailable preparing mapping '{mapping.SourceUserName}' -> '{mapping.LocalUserName}'");
                Logger.LogError(ex, "Error preparing work for mapping {Source} -> {Local}",
                    mapping.SourceUserName, mapping.LocalUserName);
            }

            processedMappings++;
            progress.Report(Math.Min(100, 100.0 * processedMappings / totalMappings));
        }

        progress.Report(100);
        return work;
    }

    /// <inheritdoc />
    protected override async Task<UserSyncItem?> BuildRecordAsync(
        UserCategoryWork source,
        IReadOnlyDictionary<(string SourceUserId, string LocalUserId, string PropertyCategory), UserSyncItem> existing,
        CancellationToken cancellationToken)
    {
        if (Client == null)
        {
            return null;
        }

        var key = (source.Mapping.SourceUserId, source.Mapping.LocalUserId, source.Category);
        existing.TryGetValue(key, out var existingItem);

        return await _builder.BuildRecordAsync(
            source.Mapping,
            source.Category,
            source.SourceUser,
            source.LocalUserDto,
            source.LocalUser,
            Client,
            ConfigManager.Configuration,
            existingItem,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override (string SourceUserId, string LocalUserId, string PropertyCategory) ExtractKey(UserSyncItem record)
        => (record.SourceUserId, record.LocalUserId, record.PropertyCategory);


    // In scope when the row's user mapping is currently enabled. Disabling a
    // mapping preserves its rows (including Ignored overrides) instead of
    // pruning them.
    /// <inheritdoc />
    protected override bool IsInScope(UserSyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        foreach (var mapping in ConfigManager.Configuration.GetEnabledUserMappings())
        {
            if (string.Equals(mapping.SourceUserId, record.SourceUserId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(mapping.LocalUserId, record.LocalUserId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
            IntervalTicks = TimeSpan.FromHours(22).Ticks
        }
    };
}
