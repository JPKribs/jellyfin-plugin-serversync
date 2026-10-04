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
        var modules = new List<(Func<IScheduledTask> Refresh, Func<IScheduledTask> Sync)>();
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

        if (modules.Count == 0)
        {
            _logger.LogInformation("{Task}: no information module is enabled, nothing to do", Name);
            progress.Report(100);
            return;
        }

        // One module failing must not starve the others, so each step is
        // caught on its own and the failures are raised together at the end,
        // which keeps the task marked failed. A module whose refresh threw
        // skips its apply, since the queue it would drain was not rebuilt.
        var failures = new List<Exception>();
        var share = 100.0 / (modules.Count * 2);
        for (var i = 0; i < modules.Count; i++)
        {
            var refreshOffset = 2 * i * share;
            var refreshed = await RunStepAsync(modules[i].Refresh, refreshOffset, share, progress, failures, cancellationToken).ConfigureAwait(false);
            if (!refreshed)
            {
                continue;
            }

            await RunStepAsync(modules[i].Sync, refreshOffset + share, share, progress, failures, cancellationToken).ConfigureAwait(false);
        }

        progress.Report(100);

        if (failures.Count > 0)
        {
            throw new AggregateException($"{Name}: {failures.Count} step(s) failed, see the log for each", failures);
        }
    }

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
            _logger.LogInformation("{Task}: running {Step}", Name, stepName);
            await task.ExecuteAsync(new Progress<double>(p => progress.Report(offset + (share * Math.Clamp(p, 0, 100) / 100.0))), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
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
