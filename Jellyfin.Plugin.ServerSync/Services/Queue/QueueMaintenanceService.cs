using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JPKribs.Jellyfin.Base;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Cleanup;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Downloads;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.Playlists;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Runs upkeep for the hint queues and the sync tables every six hours.
/// It removes what has settled or gone stale, gives errored rows fresh retries, and tells the administrator what keeps failing.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class QueueMaintenanceService : IHostedService, IDisposable
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan SettledKept = TimeSpan.FromDays(7);
    private static readonly TimeSpan PendingKept = TimeSpan.FromDays(14);

    /// <summary>How long a row stays errored before it is queued again, so one that failed a moment ago is not retried at once.</summary>
    private static readonly TimeSpan ErroredRetryAfter = TimeSpan.FromHours(1);

    /// <summary>How long a row may go on failing, retried on every run, before the administrator is told.</summary>
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
    /// <param name="outbound">Holds the sent and failed hints that expire.</param>
    /// <param name="versions">Holds the stamps that are pruned.</param>
    /// <param name="store">Runs bulk statements over the sync tables.</param>
    /// <param name="configManager">Reads the Recycling bin settings and the run failures.</param>
    /// <param name="libraryManager">Tells which items still exist.</param>
    /// <param name="userManager">Tells which users still exist.</param>
    /// <param name="services">Resolves the content table and the activity writer when a step needs them.</param>
    /// <param name="logger">Writes what each step did or could not do.</param>
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
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

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
            // The loop ends by cancellation when the service stops.
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _stopping?.Dispose();

    /// <summary>Runs one round of upkeep.</summary>
    public void RunOnce()
    {
        var now = DateTime.UtcNow;
        Step("expire settled hints", () => _outbound.ExpireSettled(now - SettledKept));
        Step("prune pending versions", () => _versions.PrunePending(now - PendingKept));
        Step("tell the administrator what keeps failing", () => ReportFailing(now));
        Step("tell the administrator what is blocked", ReportBlocked);
        Step("requeue errored rows", () => _store.RequeueErrored(now - ErroredRetryAfter));
        Step("forget old removals", () => _services.GetService<RemovedItemsStore>()?.Trim(now - RemovedItemsStore.Kept) ?? 0);
        Step("prune versions of removed items", PruneMissingVersions);
        Step("forget copies of deleted playlists", ForgetDeletedPlaylistCopies);
        Step("remove finished replacement backups", RemoveFinishedReplacementBackups);
    }

    /// <summary>
    /// Writes one Activity page entry for what has failed on every run for a day. It runs before the requeue, while those rows still read as failed.
    /// </summary>
    private int ReportFailing(DateTime now)
    {
        var failing = _store.TrackFailures(now, now - FailingBeforeTold);
        if (failing.Count == 0)
        {
            return 0;
        }

        var total = failing.Sum(kind => kind.Count);
        var example = failing.Select(kind => kind.Reason).FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason));
        string Overview(string? language)
        {
            var kinds = string.Join(", ", failing.Select(kind => CountFailing(kind, language)));
            return example is null
                ? BaseTranslations.Format(TranslationKeys.AttentionFailingOverview, language, kinds)
                : BaseTranslations.Format(TranslationKeys.AttentionFailingExample, language, kinds, example);
        }

        _logger.LogWarning("Server Sync: {Overview}", FormatUtilities.SanitizeForLog(Overview(null), 1000));
        if (_services.GetService<JPKribs.Jellyfin.Base.ActivityLogger>() is { } activity)
        {
            var language = ActivityLanguage();
            _ = activity.LogAndWaitAsync(
                BaseTranslations.Format(total == 1 ? TranslationKeys.AttentionFailingNameOne : TranslationKeys.AttentionFailingNameMany, language, total),
                "ServerSyncAttention",
                ActivityText.Sentence(Overview(language)),
                LogLevel.Warning);
        }

        return total;
    }

    /// <summary>
    /// Writes one Activity page entry for each kind of run that has ended the same wrong way for a day, such as an unreachable server.
    /// </summary>
    private int ReportBlocked()
    {
        var standing = RunFailureLog.TakeUnreported(_configManager, FailingBeforeTold);
        foreach (var failure in standing)
        {
            var phase = failure.Phase.ToLowerInvariant();
            _logger.LogWarning("Server Sync: {Overview}", FormatUtilities.SanitizeForLog(BaseTranslations.Format(TranslationKeys.AttentionHeldOverview, null, failure.ModuleKey, phase, failure.Reason), 1000));
            if (_services.GetService<JPKribs.Jellyfin.Base.ActivityLogger>() is { } activity)
            {
                var language = ActivityLanguage();
                _ = activity.LogAndWaitAsync(
                    BaseTranslations.Format(TranslationKeys.AttentionHeldName, language, failure.ModuleKey, failure.Phase),
                    "ServerSyncAttention",
                    ActivityText.Sentence(BaseTranslations.Format(TranslationKeys.AttentionHeldOverview, language, failure.ModuleKey, phase, failure.Reason)),
                    LogLevel.Warning);
            }
        }

        return standing.Count;
    }

    /// <summary>Gets the server's display language, which Activity page entries are written in.</summary>
    private string? ActivityLanguage()
        => _services.GetService<IServerConfigurationManager>() is { } config ? BaseTranslations.ServerLanguage(config) : null;

    /// <summary>Counts the failing rows of one kind, such as "3 file items", in a language.</summary>
    private static string CountFailing(FailingRows kind, string? language)
    {
        var one = kind.Count == 1;
        var key = kind.Label switch
        {
            "file" => one ? TranslationKeys.FailingFileOne : TranslationKeys.FailingFileMany,
            "watch history" => one ? TranslationKeys.FailingHistoryOne : TranslationKeys.FailingHistoryMany,
            "metadata" => one ? TranslationKeys.FailingMetadataOne : TranslationKeys.FailingMetadataMany,
            "person" => one ? TranslationKeys.FailingPersonOne : TranslationKeys.FailingPersonMany,
            _ => one ? TranslationKeys.FailingUserOne : TranslationKeys.FailingUserMany
        };
        return BaseTranslations.Format(key, language, kind.Count);
    }

    /// <summary>Forgets the copies of playlists that no longer exist here.</summary>
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

    /// <summary>
    /// Removes the half files a server stopped mid download left behind, once they have sat for a day.
    /// Such a file is never the only copy, since its download starts again from the source.
    /// </summary>
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
                    _logger.LogDebug(ex, "Could not remove the abandoned download {File}", FormatUtilities.SanitizeForLog(staged));
                }
            }
        }

        return removed;
    }

    /// <summary>Removes or recycles the backups a finished file replacement left behind, unless Sync Content is running.</summary>
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
                            _logger.LogDebug(ex, "Could not remove the replacement backup {Backup}", FormatUtilities.SanitizeForLog(backup));
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

    /// <summary>Runs upkeep after a first delay and then at every interval until stopped.</summary>
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
        // The loop ends by cancellation when the service stops.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Runs one step and logs what it did, so a failing step never stops the others.</summary>
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
        // One failing step must not stop the rest of the upkeep or its loop.
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Server Sync upkeep could not {Step}", name);
        }
    }

    /// <summary>
    /// Removes the stamps of items and users that no longer exist. People are kept, since a missing person may not be scanned in yet.
    /// </summary>
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
