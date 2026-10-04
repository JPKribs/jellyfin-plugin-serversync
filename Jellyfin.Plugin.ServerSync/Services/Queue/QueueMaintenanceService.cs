using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Background upkeep for the hint queues and the sync tables. Every six hours it removes hints that sat
/// sent or failed for a week, pending versions whose values never applied, and the versions of items and
/// users this server no longer has, and gives errored rows a fresh set of retries once a day. At start it
/// gives rows from before servers became a list the key of the server they belong to.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class QueueMaintenanceService : IHostedService, IDisposable
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan SettledKept = TimeSpan.FromDays(7);
    private static readonly TimeSpan PendingKept = TimeSpan.FromDays(14);
    private static readonly TimeSpan ErroredRetryAfter = TimeSpan.FromDays(1);

    private readonly OutboundHintStore _outbound;
    private readonly VersionStore _versions;
    private readonly MaintenanceStore _store;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<QueueMaintenanceService> _logger;
    private CancellationTokenSource? _stopping;
    private Task? _loop;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueMaintenanceService"/> class.
    /// </summary>
    /// <param name="outbound">The outbound hint store.</param>
    /// <param name="versions">The version store.</param>
    /// <param name="store">Bulk statements over the sync tables.</param>
    /// <param name="configManager">Plugin configuration.</param>
    /// <param name="libraryManager">The library, to tell which items still exist.</param>
    /// <param name="userManager">The users, to tell which still exist.</param>
    /// <param name="logger">Logger.</param>
    public QueueMaintenanceService(
        OutboundHintStore outbound,
        VersionStore versions,
        MaintenanceStore store,
        IPluginConfigurationManager configManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<QueueMaintenanceService> logger)
    {
        _outbound = outbound;
        _versions = versions;
        _store = store;
        _configManager = configManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping is not null)
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on stop.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _stopping?.Dispose();

    /// <summary>One round of upkeep. Public so tests and the dashboard can run it on demand.</summary>
    public void RunOnce()
    {
        var now = DateTime.UtcNow;
        Step("backfill server keys", () =>
        {
            var first = _configManager.Configuration.GetPullServers().FirstOrDefault();
            return first is null ? 0 : _store.BackfillServerKey(first.Key);
        });
        Step("expire settled hints", () => _outbound.ExpireSettled(now - SettledKept));
        Step("prune pending versions", () => _versions.PrunePending(now - PendingKept));
        Step("requeue errored rows", () => _store.RequeueErrored(now - ErroredRetryAfter));
        Step("prune versions of removed items", PruneMissingVersions);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(FirstRunDelay, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                RunOnce();
                await Task.Delay(Interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    private void Step(string name, Func<int> step)
    {
        try
        {
            var count = step();
            if (count > 0)
            {
                _logger.LogInformation("Server Sync upkeep: {Step}, {Count} row(s)", name, count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Server Sync upkeep could not {Step}", name);
        }
    }

    // Versions of items and users that no longer exist decide nothing and only grow the table. People are
    // kept, since a person's key is a name and a missing person may simply not be scanned in yet.
    private int PruneMissingVersions()
    {
        var removed = 0;
        foreach (var kind in new[] { HintKind.Metadata, HintKind.Content })
        {
            var gone = _versions.GetKeys(kind).Where(k => Guid.TryParse(k, out var id) && _libraryManager.GetItemById(id) is null).ToList();
            removed += _versions.Remove(kind, gone);
        }

        var goneHistory = new List<string>();
        foreach (var key in _versions.GetKeys(HintKind.History))
        {
            if (HintProtocol.TryParseHistoryKey(key, out var userId, out var itemId)
                && (_userManager.GetUserById(userId) is null || _libraryManager.GetItemById(itemId) is null))
            {
                goneHistory.Add(key);
            }
        }

        removed += _versions.Remove(HintKind.History, goneHistory);
        var goneUsers = _versions.GetKeys(HintKind.Users).Where(k => Guid.TryParse(k, out var id) && _userManager.GetUserById(id) is null).ToList();
        removed += _versions.Remove(HintKind.Users, goneUsers);
        return removed;
    }
}
