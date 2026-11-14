using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Playlists;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Background upkeep for the hint queues and the sync tables. Every six hours it removes hints that sat
/// sent or failed for a week, pending versions whose values never applied, and the versions of items and
/// users this server no longer has, gives rows that errored more than a day ago a fresh set of retries,
/// forgets copies of playlists that were deleted, and removes the backups a finished file replacement
/// left behind. It also gives rows from before servers became a list the key of the server they belong to.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class QueueMaintenanceService : IHostedService, IDisposable
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan SettledKept = TimeSpan.FromDays(7);
    private static readonly TimeSpan PendingKept = TimeSpan.FromDays(14);
    // A failed row is tried again on the next run rather than waiting for someone to press Retry. The
    // hour keeps a row that failed a moment ago from being queued again in the same breath.
    private static readonly TimeSpan ErroredRetryAfter = TimeSpan.FromHours(1);

    // How long a row may go on failing, retried on every run, before the administrator is told.
    private static readonly TimeSpan FailingBeforeTold = TimeSpan.FromDays(1);
    private static readonly TimeSpan ReplacementBackupKept = TimeSpan.FromDays(1);
    private const string ReplacementMarker = ".replacing.";

    private readonly OutboundHintStore _outbound;
    private readonly VersionStore _versions;
    private readonly MaintenanceStore _store;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IServiceProvider _services;
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
    /// <param name="services">The service provider the content table is read from.</param>
    /// <param name="logger">Logger.</param>
    public QueueMaintenanceService(
        OutboundHintStore outbound,
        VersionStore versions,
        MaintenanceStore store,
        IPluginConfigurationManager configManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IServiceProvider services,
        ILogger<QueueMaintenanceService> logger)
    {
        _services = services;
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
        Step("mark the copies tracked before origins were recorded", MarkExistingCopies);
        Step("expire settled hints", () => _outbound.ExpireSettled(now - SettledKept));
        Step("prune pending versions", () => _versions.PrunePending(now - PendingKept));
        // Before the requeue, while the rows that failed on the last run still read as failed.
        Step("tell the administrator what keeps failing", () => ReportFailing(now));
        Step("tell the administrator what is blocked", ReportBlocked);
        Step("requeue errored rows", () => _store.RequeueErrored(now - ErroredRetryAfter));
        Step("forget old removals", () => _services.GetService<RemovedItemsStore>()?.Trim(now - RemovedItemsStore.Kept) ?? 0);
        Step("prune versions of removed items", PruneMissingVersions);
        Step("forget copies of deleted playlists", ForgetDeletedPlaylistCopies);
        Step("remove finished replacement backups", RemoveFinishedReplacementBackups);
    }

    // One entry on Jellyfin's Activity page for what has failed on every run for a day. Everything
    // else is retried quietly, so this is the only time a failing row asks for anyone's attention.
    private int ReportFailing(DateTime now)
    {
        var failing = _store.TrackFailures(now, now - FailingBeforeTold);
        if (failing.Count == 0)
        {
            return 0;
        }

        var total = failing.Sum(kind => kind.Count);
        var kinds = string.Join(", ", failing.Select(kind => $"{kind.Count} {kind.Label}{(kind.Count == 1 ? string.Empty : " items")}"));
        var example = failing.Select(kind => kind.Reason).FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason));
        var overview = $"{kinds} have failed on every run for a day and are still being retried. Open Server Sync and filter the Sync tab by Errored to see each one"
            + (example is null ? string.Empty : $". For example: {example}");
        _logger.LogWarning("Server Sync: {Overview}", overview);
        if (_services.GetService<JPKribs.Jellyfin.Base.ActivityLogger>() is { } activity)
        {
            _ = activity.LogAndWaitAsync($"Server Sync Needs Attention: {total} Item{(total == 1 ? string.Empty : "s")} Keep Failing", "ServerSyncAttention", JPKribs.Jellyfin.Base.ActivityText.Sentence(overview), LogLevel.Warning);
        }

        return total;
    }

    // One entry on the Activity page for each kind of run that has ended the same wrong way for a
    // day: a cleanup held back by a safety limit, a server that cannot be reached, a disk too full to
    // download to. A clean run clears the failure, so it is only raised again if it comes back.
    private int ReportBlocked()
    {
        var standing = RunFailureLog.TakeUnreported(_configManager, FailingBeforeTold);
        foreach (var failure in standing)
        {
            var overview = $"{failure.ModuleKey} {failure.Phase.ToLowerInvariant()} has ended this way on every run for a day: {failure.Reason}";
            _logger.LogWarning("Server Sync: {Overview}", overview);
            if (_services.GetService<JPKribs.Jellyfin.Base.ActivityLogger>() is { } activity)
            {
                _ = activity.LogAndWaitAsync($"Server Sync Needs Attention: {failure.ModuleKey} {failure.Phase} Is Held Up", "ServerSyncAttention", JPKribs.Jellyfin.Base.ActivityText.Sentence(overview), LogLevel.Warning);
            }
        }

        return standing.Count;
    }

    // Once, after the update that records where a copy came from. A copy tracked before it says
    // neither, so each is looked at: one that never went through a download was on this server
    // already and is its own, and one that did was given by the server its row names. From then on
    // the old rows follow the same rules as new ones: a server's own file stays when another removes
    // theirs, and a download is known as its origin's file.
    private int MarkExistingCopies()
    {
        var config = _configManager.Configuration;
        if (config.ExistingCopiesMarked)
        {
            return 0;
        }

        var content = SyncModuleMutex.ForModule("Content");
        if (!content.Wait(0))
        {
            return 0;
        }

        var marked = 0;
        try
        {
            marked = ExistingCopies.Mark(_services.GetRequiredService<ContentSyncTableManager>(), config);
        }
        finally
        {
            content.Release();
        }

        config.ExistingCopiesMarked = true;
        _configManager.SaveConfiguration();
        return marked;
    }

    private int ForgetDeletedPlaylistCopies()
    {
        var forgotten = 0;
        foreach (var id in PlaylistCopyStore.Ids())
        {
            if (_libraryManager.GetItemById(id) is not Playlist)
            {
                PlaylistCopyStore.Remove(id);
                forgotten++;
            }
        }

        return forgotten;
    }

    // A download is copied into the library folder under "name.syncing.<id>" and renamed into place.
    // A server stopped mid-copy leaves that half file behind, and only another download of the same
    // file would clear it. Any that has sat for a day is removed. It is never the only copy of
    // anything: the download it belonged to starts again from the source.
    private int RemoveAbandonedStagedFiles(ContentSyncTableManager manager, PluginConfiguration config, DateTime cutoff)
    {
        var removed = 0;
        var folders = manager.GetAllStrict()
            .Select(row => string.IsNullOrEmpty(row.LocalPath) ? null : Path.GetDirectoryName(row.LocalPath))
            .Where(folder => !string.IsNullOrEmpty(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var staged in Directory.EnumerateFiles(folder!, "*.syncing.*"))
            {
                var name = Path.GetFileName(staged);
                var id = name[(name.LastIndexOf(".syncing.", StringComparison.Ordinal) + ".syncing.".Length)..];
                if (id.Length != 32 || !id.All(Uri.IsHexDigit) || File.GetLastWriteTimeUtc(staged) > cutoff || !FileValidationService.IsPathWithinLibrary(staged, config))
                {
                    continue;
                }

                try
                {
                    File.Delete(staged);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Could not remove the abandoned download {File}", staged);
                }
            }
        }

        return removed;
    }

    private int RemoveFinishedReplacementBackups()
    {
        var content = SyncModuleMutex.ForModule("Content");
        if (!content.Wait(0))
        {
            return 0;
        }

        try
        {
            var config = _configManager.Configuration;
            var manager = _services.GetRequiredService<ContentSyncTableManager>();
            var cutoff = DateTime.UtcNow - ReplacementBackupKept;
            var removed = RemoveAbandonedStagedFiles(manager, config, cutoff);
            foreach (var row in manager.GetByStatus(SyncStatus.Synced))
            {
                if (string.IsNullOrEmpty(row.LocalPath)
                    || !File.Exists(row.LocalPath)
                    || (row.SourceSize > 0 && new FileInfo(row.LocalPath).Length != row.SourceSize)
                    || Path.GetDirectoryName(row.LocalPath) is not { Length: > 0 } folder)
                {
                    continue;
                }

                var paths = new[] { row.LocalPath }.Concat(FileOperationUtilities.GetCompanionFiles(row.LocalPath));
                foreach (var original in paths)
                {
                    foreach (var backup in Directory.EnumerateFiles(folder, Path.GetFileName(original) + ReplacementMarker + "*"))
                    {
                        if (File.GetLastWriteTimeUtc(backup) > cutoff || !FileValidationService.IsPathWithinLibrary(backup, config))
                        {
                            continue;
                        }

                        try
                        {
                            if (config.EnableRecyclingBin && !string.IsNullOrEmpty(config.RecyclingBinPath))
                            {
                                RecyclingBinService.ArchiveBackupToRecyclingBin(backup, original, config.RecyclingBinPath, _logger);
                            }
                            else
                            {
                                File.Delete(backup);
                            }

                            removed++;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            _logger.LogDebug(ex, "Could not remove the replacement backup {Backup}", backup);
                        }
                    }
                }
            }

            return removed;
        }
        finally
        {
            content.Release();
        }
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

        var goneHistory = _versions.GetKeys(HintKind.History)
            .Where(key => HintProtocol.TryParseHistoryKey(key, out var userId, out var itemId)
                && (_userManager.GetUserById(userId) is null || _libraryManager.GetItemById(itemId) is null))
            .ToList();

        removed += _versions.Remove(HintKind.History, goneHistory);
        var goneUsers = _versions.GetKeys(HintKind.Users).Where(k => Guid.TryParse(k, out var id) && _userManager.GetUserById(id) is null).ToList();
        removed += _versions.Remove(HintKind.Users, goneUsers);
        return removed;
    }
}
