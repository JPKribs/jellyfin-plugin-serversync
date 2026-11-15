using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

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
    /// Initializes a new instance of the <see cref="RefreshUserSyncTableTask"/> class.
    /// </summary>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="clientFactory">Builds the client for each source server.</param>
    /// <param name="userManager">Reads Jellyfin's users.</param>
    /// <param name="builder">Builds user sync rows.</param>
    /// <param name="manager">Reads and writes the sync table.</param>
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
    public override string Name => "Scan Users";

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
            && config.GetPullServers().Count > 0
            && config.GetEnabledUserMappings().Count > 0;
    }

    /// <inheritdoc />
    protected override async Task<IList<UserCategoryWork>> GetListAsync(ScanSource source, IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(progress);

        var config = ConfigManager.Configuration;
        var client = source.Client;
        var enabledMappings = source.Server.GetEnabledUserMappings();
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

                var sourceUser = await client.GetUserAsync(sourceUserId, cancellationToken).ConfigureAwait(false);
                if (sourceUser == null)
                {
                    // Source user fetch returned null (a transient API failure and a
                    // genuinely missing user look the same). Skip pruning this run
                    // so a network blip doesn't delete the mapping's rows.
                    MarkSourceUnavailable($"source server unavailable, source user '{mapping.SourceUserName}' not returned");
                    Logger.LogWarning("Source user {Id} not found. Skipping mapping {Source} -> {Local}",
                        mapping.SourceUserId, mapping.SourceUserName, mapping.LocalUserName);
                    continue;
                }

                var localUser = _userManager.GetUserById(localUserId);
                if (localUser == null)
                {
                    // Local target gone, so this mapping can't be enumerated. Skip
                    // pruning this run rather than treat its rows as deleted.
                    MarkSourceUnavailable($"local user '{mapping.LocalUserName}' not found. Mapping rows cannot be re-confirmed");
                    Logger.LogWarning("Local user {Id} not found. Skipping mapping {Source} -> {Local}",
                        mapping.LocalUserId, mapping.SourceUserName, mapping.LocalUserName);
                    continue;
                }

                var localUserDto = _userManager.GetUserDto(localUser);

                if (config.UserSyncPolicy)
                {
                    work.Add(new UserCategoryWork
                    {
                        Source = source,
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
                        Source = source,
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
                        Source = source,
                        Mapping = mapping,
                        Category = UserPropertyCategory.ProfileImage,
                        SourceUser = sourceUser,
                        LocalUserDto = localUserDto,
                        LocalUser = localUser
                    });
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
        var key = (source.Mapping.SourceUserId, source.Mapping.LocalUserId, source.Category);
        existing.TryGetValue(key, out var existingItem);

        var record = await _builder.BuildRecordAsync(
            source.Mapping,
            source.Category,
            source.SourceUser,
            source.LocalUserDto,
            source.LocalUser,
            source.Source.Client,
            ConfigManager.Configuration,
            existingItem,
            cancellationToken,
            source.Source.Server.GetEnabledLibraryMappings(),
            source.Observe ? null : BothWaysRule(source.Source.Server)).ConfigureAwait(false);

        if (record != null)
        {
            record.ServerKey = source.Source.Key;
            record.Observed = source.Observe;
        }

        return record;
    }

    private bool Newest => ConfigManager.Configuration.UserSyncPrecedence == SyncPrecedence.Newest;

    /// <summary>
    /// Settings go both ways with a Sync server: a change made here stays until that server takes it.
    /// From a Pull server they only ever arrive.
    /// </summary>
    private UserConflictRule? BothWaysRule(SourceServer server)
        => Newest && server.Mode == ServerMode.Sync ? ConfigManager.Configuration.UserSyncConflict : null;

    /// <summary>
    /// One server speaks for a local user's settings on each run: the highest ranked one that changed
    /// them since this server last looked. When none has, the server whose settings were applied last
    /// goes on speaking, so a lower ranked server's change is not undone by a higher one that has
    /// nothing new. Rank decides before anything has been applied.
    /// </summary>
    /// <inheritdoc />
    protected override Task<IReadOnlyDictionary<string, UserCategoryWork>> ContestAsync(IReadOnlyDictionary<string, IReadOnlyList<UserCategoryWork>> contested, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contested);
        var winners = new Dictionary<string, UserCategoryWork>(StringComparer.OrdinalIgnoreCase);
        if (!Newest)
        {
            return Task.FromResult<IReadOnlyDictionary<string, UserCategoryWork>>(winners);
        }

        foreach (var (key, offers) in contested)
        {
            var speaking = offers.FirstOrDefault(HasChangedSinceSeen)
                ?? offers.Where(o => Seen(o)?.LastSyncTime is not null).MaxBy(o => Seen(o)!.LastSyncTime)
                ?? offers[0];
            if (!ReferenceEquals(speaking, offers[0]))
            {
                winners[key] = speaking;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, UserCategoryWork>>(winners);
    }

    /// <summary>
    /// A server passed over before this server has ever looked at it is recorded, so the next run can
    /// tell whether it has changed since. One already recorded and unchanged needs nothing.
    /// </summary>
    /// <inheritdoc />
    protected override UserCategoryWork? ObservedOffer(UserCategoryWork passedOver, UserCategoryWork speaking)
    {
        ArgumentNullException.ThrowIfNull(passedOver);
        if (!Newest || Seen(passedOver) is not null || passedOver.Category == UserPropertyCategory.ProfileImage)
        {
            return null;
        }

        return new UserCategoryWork
        {
            Source = passedOver.Source,
            Mapping = passedOver.Mapping,
            Category = passedOver.Category,
            SourceUser = passedOver.SourceUser,
            LocalUserDto = passedOver.LocalUserDto,
            LocalUser = passedOver.LocalUser,
            Observe = true
        };
    }

    /// <inheritdoc />
    protected override bool MayOutrankOwner(UserSyncItem record) => Newest;

    /// <summary>
    /// Each server's row records what was last seen of it, which is how it is known to have changed.
    /// </summary>
    /// <inheritdoc />
    protected override bool CanRetire(UserSyncItem record) => !Newest && base.CanRetire(record);

    /// <inheritdoc />
    protected override void DecideStatus(UserSyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Observed)
        {
            if (record.Status != SyncStatus.Ignored)
            {
                record.Status = SyncStatus.Synced;
                record.Reason = "seen: another server speaks for this user's settings";
            }

            return;
        }

        base.DecideStatus(record);
        if (record.KeepAgreedValue && record.Status == SyncStatus.Synced)
        {
            record.Reason = "kept: changed on this server, and the other server has not changed it since the two agreed";
        }
    }

    private UserSyncItem? Seen(UserCategoryWork offer)
        => ExistingRows.TryGetValue((offer.Mapping.SourceUserId, offer.Mapping.LocalUserId, offer.Category), out var row) ? row : null;

    private bool HasChangedSinceSeen(UserCategoryWork offer)
    {
        if (Seen(offer) is not { } seen)
        {
            return false;
        }

        var now = offer.Category switch
        {
            UserPropertyCategory.Policy => UserSyncMergeService.ExtractPolicyJson(offer.SourceUser.Policy),
            UserPropertyCategory.Configuration => UserSyncMergeService.ExtractConfigurationJson(offer.SourceUser.Configuration),
            _ => null
        };
        return now is not null && !new Models.Common.Comparators.JsonBlobComparator().Equals(seen.SourceValue, now);
    }

    /// <summary>
    /// Two servers collide when both map a user onto the same local user. The higher ranked
    /// server's user supplies that local user's settings.
    /// </summary>
    /// <inheritdoc />
    protected override string? PriorityKeyOf(UserCategoryWork source)
        => source is null ? null : source.Mapping.LocalUserId + "|" + source.Category;

    /// <inheritdoc />
    protected override string? PriorityKeyOf(UserSyncItem record)
        => record is null ? null : record.LocalUserId + "|" + record.PropertyCategory;

    /// <inheritdoc />
    protected override (string SourceUserId, string LocalUserId, string PropertyCategory) ExtractKey(UserSyncItem record)
        => (record.SourceUserId, record.LocalUserId, record.PropertyCategory);

    /// <summary>
    /// In scope when the row's user mapping is currently enabled. Disabling a
    /// mapping preserves its rows (including Ignored overrides) instead of
    /// pruning them.
    /// </summary>
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
    protected override void RecordRunCompleted(PluginConfiguration config, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.LastUserSyncTime = utcNow;
    }
}
