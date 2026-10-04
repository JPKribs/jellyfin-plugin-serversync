using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Tasks;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Applies a users hint: a mapped user's policy, configuration, or profile image changed on the
/// origin. One row per enabled category is built the way the scan would, the conflict is decided once
/// for the user on origin versions, and the rows that queued are applied the way a run would.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class UserHintHandler
{
    private readonly IServiceProvider _services;
    private readonly IPluginConfigurationManager _configManager;
    private readonly IUserManager _userManager;
    private readonly VersionConflictResolver _resolver;
    private readonly LocalHintPublisher _publisher;
    private readonly UserSyncTableManager _table;
    private readonly ILogger<UserHintHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserHintHandler"/> class.
    /// </summary>
    /// <param name="services">The service provider the module tasks are built from.</param>
    /// <param name="configManager">Plugin configuration.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="resolver">The version resolver.</param>
    /// <param name="publisher">Publishes this server's winning values.</param>
    /// <param name="table">The user table.</param>
    /// <param name="logger">Logger.</param>
    public UserHintHandler(
        IServiceProvider services,
        IPluginConfigurationManager configManager,
        IUserManager userManager,
        VersionConflictResolver resolver,
        LocalHintPublisher publisher,
        UserSyncTableManager table,
        ILogger<UserHintHandler> logger)
    {
        _services = services;
        _configManager = configManager;
        _userManager = userManager;
        _resolver = resolver;
        _publisher = publisher;
        _table = table;
        _logger = logger;
    }

    /// <summary>Applies one users hint.</summary>
    /// <param name="hint">The inbound row.</param>
    /// <param name="origin">The configured entry for the origin.</param>
    /// <param name="client">A client bound to the origin.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async Task<HintApplyResult> ApplyAsync(InboundHint hint, SourceServer origin, SourceServerClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hint);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(client);

        var config = _configManager.Configuration;
        if (!config.EnableUserSync)
        {
            return HintApplyResult.Dropped("user sync is off on this server");
        }

        if (!Guid.TryParse(hint.Key, out var originUserId))
        {
            return HintApplyResult.Dropped("the hint's key is not a user id");
        }

        var mapping = HintMapping.FindBySourceUser(origin, originUserId);
        if (mapping is null || !Guid.TryParse(mapping.LocalUserId, out var localUserId))
        {
            return HintApplyResult.Dropped($"user '{hint.UserName ?? hint.Key}' on '{origin.DisplayName}' is not mapped here");
        }

        var sourceUser = await client.GetUserAsync(originUserId, cancellationToken).ConfigureAwait(false);
        if (sourceUser is null)
        {
            return HintApplyResult.RetryLater($"could not read the user from '{origin.DisplayName}', or the user no longer exists");
        }

        var localUser = _userManager.GetUserById(localUserId);
        if (localUser is null)
        {
            return HintApplyResult.Dropped($"local user '{mapping.LocalUserName}' no longer exists");
        }

        var localDto = _userManager.GetUserDto(localUser);
        var categories = new List<string>();
        if (config.UserSyncPolicy)
        {
            categories.Add(UserPropertyCategory.Policy);
        }

        if (config.UserSyncConfiguration)
        {
            categories.Add(UserPropertyCategory.Configuration);
        }

        if (config.UserSyncProfileImage)
        {
            categories.Add(UserPropertyCategory.ProfileImage);
        }

        if (categories.Count == 0)
        {
            return HintApplyResult.Dropped("no user sync category is enabled on this server");
        }

        var source = new ScanSource(origin, client, Math.Max(0, config.GetPullServers().FindIndex(s => string.Equals(s.Key, origin.Key, StringComparison.OrdinalIgnoreCase))));
        var refresh = ActivatorUtilities.CreateInstance<RefreshUserSyncTableTask>(_services);
        var queued = new List<UserSyncItem>();
        foreach (var category in categories)
        {
            var work = new UserCategoryWork { Source = source, Mapping = mapping, Category = category, SourceUser = sourceUser, LocalUserDto = localDto, LocalUser = localUser };
            var record = await refresh.RefreshOneAsync(work, cancellationToken).ConfigureAwait(false);
            if (record is not null && record.Status == SyncStatus.Queued)
            {
                queued.Add(record);
            }
        }

        var localKey = HintProtocol.UsersKey(localUserId);
        var incoming = new ObjectVersion { Kind = HintKind.Users, Key = localKey, ServerId = hint.VersionServerId, Timestamp = hint.VersionTimestamp };
        if (queued.Count == 0)
        {
            _resolver.Record(incoming);
            return HintApplyResult.Unchanged;
        }

        var local = _resolver.Recorded(HintKind.Users, localKey);
        if (local is not null && VersionDecider.Decide(local, incoming, valuesEqual: false) == VersionDecision.Keep)
        {
            foreach (var record in queued)
            {
                record.Status = SyncStatus.Synced;
                record.StatusDate = DateTime.UtcNow;
                record.Reason = $"kept: this server's edit is newer than the one on '{origin.DisplayName}', which will pull it";
                _table.Upsert(record);
            }

            _publisher.PublishUsers(localUserId, localUser.Username, local, excludePeerKey: null);
            _logger.LogInformation("Kept this server's settings for user {User}, newer than '{Origin}', and told the peers to pull them", localUser.Username, origin.DisplayName);
            return new HintApplyResult(HintApplyOutcome.Unchanged, "this server's edit is newer");
        }

        var apply = ActivatorUtilities.CreateInstance<SyncMissingUserTask>(_services);
        var failures = new List<string>();
        foreach (var record in queued)
        {
            if (!await apply.ApplyRowAsync(record, source, cancellationToken).ConfigureAwait(false))
            {
                failures.Add($"{record.PropertyCategory}: {record.Reason ?? "failed"}");
            }
        }

        if (failures.Count > 0)
        {
            return HintApplyResult.RetryLater(string.Join("; ", failures));
        }

        _resolver.Record(incoming);
        _logger.LogInformation("Applied a users hint from '{Origin}' for {User}: {Categories}", origin.DisplayName, localUser.Username, string.Join(", ", queued.Select(q => q.PropertyCategory)));
        return HintApplyResult.Applied;
    }
}
