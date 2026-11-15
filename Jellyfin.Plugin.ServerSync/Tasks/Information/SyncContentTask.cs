using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Tasks.ContentSync;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.Information;

/// <summary>
/// Scans every source for content, downloads what is queued, processes deletions, then syncs collections, under one progress bar.
/// It is the scheduled safety net for replacements, removals, and anything that arrived while the plugin was down.
/// </summary>
public class SyncContentTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly IServiceProvider _services;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ILogger<SyncContentTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncContentTask"/> class.
    /// </summary>
    /// <param name="services">The service provider the module tasks are built from.</param>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="logger">Writes log entries.</param>
    public SyncContentTask(IServiceProvider services, IPluginConfigurationManager configManager, ILogger<SyncContentTask> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(logger);

        _services = services;
        _configManager = configManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Sync Content";

    /// <inheritdoc />
    public string Key => "ServerSyncContent";

    /// <inheritdoc />
    public string Description => "Finds added, changed, and removed files on each Pull or Sync server, then downloads, removes, and updates collections to match.";

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

        if (!_configManager.Configuration.EnableContentSync)
        {
            _logger.LogInformation("{Task}: content sync is disabled, nothing to do", Name);
            progress.Report(100);
            return;
        }

        var failures = new List<Exception>();
        var refreshed = await RunStepAsync(() => ActivatorUtilities.CreateInstance<UpdateSyncTablesTask>(_services), 0, 25, progress, failures, cancellationToken).ConfigureAwait(false);
        // A failed scan leaves the queue stale, so the download is skipped.
        if (refreshed)
        {
            await RunStepAsync(() => ActivatorUtilities.CreateInstance<SyncMissingContentTask>(_services), 25, 65, progress, failures, cancellationToken).ConfigureAwait(false);
        }

        await RunStepAsync(() => ActivatorUtilities.CreateInstance<SyncCollectionsTask>(_services), 90, 10, progress, failures, cancellationToken).ConfigureAwait(false);

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
        var stepName = "a content step";
        try
        {
            var task = create();
            stepName = task.Name;
            _logger.LogInformation("{Task}: running {Step}", Name, stepName);
            await task.ExecuteAsync(new Progress<double>(p => progress.Report(offset + (share * Math.Clamp(p, 0, 100) / 100.0))), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "{Task}: {Step} failed, continuing with the remaining steps", Name, stepName);
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
