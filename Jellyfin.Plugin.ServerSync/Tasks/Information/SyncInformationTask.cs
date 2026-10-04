using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// The scheduled safety net under the hint pipeline. Runs the full comparison for metadata, people,
/// users, and history, refresh then apply for each, under one progress bar, so anything the hints
/// missed, including changes made while the plugin was down, is caught on a schedule. The per module
/// tasks it runs are hidden from the task list and keep no schedule of their own.
/// </summary>
public class SyncInformationTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly IServiceProvider _services;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ILogger<SyncInformationTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncInformationTask"/> class.
    /// </summary>
    /// <param name="services">The service provider the module tasks are built from.</param>
    /// <param name="configManager">Plugin configuration.</param>
    /// <param name="logger">Logger.</param>
    public SyncInformationTask(IServiceProvider services, IPluginConfigurationManager configManager, ILogger<SyncInformationTask> logger)
    {
        _services = services;
        _configManager = configManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Sync Information";

    /// <inheritdoc />
    public string Key => "ServerSyncInformation";

    /// <inheritdoc />
    public string Description => "Runs the full metadata, people, user, and watch history comparison with every scan server, refresh then apply, as the safety net under change hints.";

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
        var steps = new List<Func<IScheduledTask>>();
        if (config.EnableMetadataSync)
        {
            steps.Add(() => ActivatorUtilities.CreateInstance<RefreshMetadataSyncTableTask>(_services));
            steps.Add(() => ActivatorUtilities.CreateInstance<SyncMissingMetadataTask>(_services));
        }

        if (config.EnablePeopleSync)
        {
            steps.Add(() => ActivatorUtilities.CreateInstance<RefreshPeopleSyncTableTask>(_services));
            steps.Add(() => ActivatorUtilities.CreateInstance<SyncMissingPeopleTask>(_services));
        }

        if (config.EnableUserSync)
        {
            steps.Add(() => ActivatorUtilities.CreateInstance<RefreshUserSyncTableTask>(_services));
            steps.Add(() => ActivatorUtilities.CreateInstance<SyncMissingUserTask>(_services));
        }

        if (config.EnableHistorySync)
        {
            steps.Add(() => ActivatorUtilities.CreateInstance<RefreshHistorySyncTableTask>(_services));
            steps.Add(() => ActivatorUtilities.CreateInstance<SyncMissingHistoryTask>(_services));
        }

        if (steps.Count == 0)
        {
            _logger.LogInformation("{Task}: no information module is enabled, nothing to do", Name);
            progress.Report(100);
            return;
        }

        var share = 100.0 / steps.Count;
        for (var i = 0; i < steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = i * share;
            var task = steps[i]();
            _logger.LogInformation("{Task}: running {Step}", Name, task.Name);
            await task.ExecuteAsync(new Progress<double>(p => progress.Report(offset + (share * Math.Clamp(p, 0, 100) / 100.0))), cancellationToken).ConfigureAwait(false);
        }

        progress.Report(100);
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
