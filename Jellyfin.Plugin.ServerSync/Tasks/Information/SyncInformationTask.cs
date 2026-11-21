using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Tasks.HistorySync;
using Jellyfin.Plugin.ServerSync.Tasks.MetadataSync;
using Jellyfin.Plugin.ServerSync.Tasks.PeopleSync;
using Jellyfin.Plugin.ServerSync.Tasks.PlaylistSync;
using Jellyfin.Plugin.ServerSync.Tasks.UserSync;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.Information;

/// <summary>
/// Runs the full comparison for metadata, people, users, history, and playlists, scan then apply for each, under one progress bar.
/// It is the scheduled safety net for anything the hints missed, including changes made while the plugin was down.
/// </summary>
public class SyncInformationTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly IServiceProvider _services;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ILogger<SyncInformationTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncInformationTask"/> class.
    /// </summary>
    /// <param name="services">The service provider the sync type tasks are built from.</param>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="logger">Writes log entries.</param>
    public SyncInformationTask(IServiceProvider services, IPluginConfigurationManager configManager, ILogger<SyncInformationTask> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(logger);

        _services = services;
        _configManager = configManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Sync Information";

    /// <inheritdoc />
    public string Key => "ServerSyncInformation";

    /// <inheritdoc />
    public string Description => "Compares metadata, people, user settings, watch history, and playlists with each Pull or Sync server and applies the differences.";

    /// <inheritdoc />
    public string Category => "Server Sync";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var config = _configManager.Configuration;
        var modules = new List<(Func<IScheduledTask> Refresh, Func<IScheduledTask>? Sync)>();
        if (config.EnableMetadataSync)
        {
            modules.Add((
                () => ActivatorUtilities.CreateInstance<RefreshMetadataSyncTableTask>(_services),
                () => ActivatorUtilities.CreateInstance<SyncMissingMetadataTask>(_services)));
        }

        if (config.EnablePeopleSync)
        {
            modules.Add((
                () => ActivatorUtilities.CreateInstance<RefreshPeopleSyncTableTask>(_services),
                () => ActivatorUtilities.CreateInstance<SyncMissingPeopleTask>(_services)));
        }

        if (config.EnableUserSync)
        {
            modules.Add((
                () => ActivatorUtilities.CreateInstance<RefreshUserSyncTableTask>(_services),
                () => ActivatorUtilities.CreateInstance<SyncMissingUserTask>(_services)));
        }

        if (config.EnableHistorySync)
        {
            modules.Add((
                () => ActivatorUtilities.CreateInstance<RefreshHistorySyncTableTask>(_services),
                () => ActivatorUtilities.CreateInstance<SyncMissingHistoryTask>(_services)));
        }

        if (config.EnablePlaylistSync)
        {
            modules.Add((() => ActivatorUtilities.CreateInstance<SyncPlaylistsTask>(_services), null));
        }

        if (modules.Count == 0)
        {
            _logger.LogDebug("{Task}: no information module is enabled, nothing to do", Name);
            progress.Report(100);
            return;
        }

        var failures = new List<Exception>();
        var share = 100.0 / modules.Sum(m => m.Sync is null ? 1 : 2);
        var offset = 0.0;
        foreach (var (refresh, sync) in modules)
        {
            var refreshed = await RunStepAsync(refresh, offset, share, progress, failures, cancellationToken).ConfigureAwait(false);
            offset += share;
            if (sync is null)
            {
                continue;
            }

            // A failed scan leaves the queue stale, so the apply is skipped.
            if (refreshed)
            {
                await RunStepAsync(sync, offset, share, progress, failures, cancellationToken).ConfigureAwait(false);
            }

            offset += share;
        }

        progress.Report(100);

        if (failures.Count > 0)
        {
            throw new AggregateException($"{Name}: {JPKribs.Jellyfin.Base.ActivityText.Count(failures.Count, "step")} failed, see the log for each", failures);
        }
    }

    /// <summary>Runs one step and collects its failure, so the remaining steps still run and the task still shows failed.</summary>
    private async Task<bool> RunStepAsync(
        Func<IScheduledTask> create,
        double offset,
        double share,
        IProgress<double> progress,
        List<Exception> failures,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stepName = "a module step";
        try
        {
            var task = create();
            stepName = task.Name;
            _logger.LogDebug("{Task}: running {Step}", Name, stepName);
            await task.ExecuteAsync(new Progress<double>(p => progress.Report(offset + (share * Math.Clamp(p, 0, 100) / 100.0))), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "{Task}: {Step} failed, continuing with the remaining modules", Name, stepName);
            failures.Add(ex);
            progress.Report(offset + share);
            return false;
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks
        }
    };
}
