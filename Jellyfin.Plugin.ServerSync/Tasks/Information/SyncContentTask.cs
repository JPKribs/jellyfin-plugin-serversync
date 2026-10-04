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
/// The scheduled content pass: refresh the content table from every scan server, download what is
/// queued, process deletions, then sync collections, under one progress bar. Content hints bring
/// new files over between runs. This task is the safety net that also handles replacements,
/// removals, and anything that arrived while the plugin was down.
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
    /// <param name="configManager">Plugin configuration.</param>
    /// <param name="logger">Logger.</param>
    public SyncContentTask(IServiceProvider services, IPluginConfigurationManager configManager, ILogger<SyncContentTask> logger)
    {
        _services = services;
        _configManager = configManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Sync Content";

    /// <inheritdoc />
    public string Key => "ServerSyncContent";

    /// <inheritdoc />
    public string Description => "Refreshes the content table from every scan server, downloads queued files, processes deletions, and syncs collections.";

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

        // The refresh and the download get most of the bar. Collections are quick.
        // Each step is caught on its own so one failure does not skip the
        // rest, and the failures are raised together at the end so the task
        // still shows failed. The download is skipped when the refresh threw,
        // since the queue it would drain was not rebuilt. Collections mirror
        // what is already local, so they run either way.
        var failures = new List<Exception>();
        var refreshed = await RunStepAsync(() => ActivatorUtilities.CreateInstance<UpdateSyncTablesTask>(_services), 0, 25, progress, failures, cancellationToken).ConfigureAwait(false);
        if (refreshed)
        {
            await RunStepAsync(() => ActivatorUtilities.CreateInstance<SyncMissingContentTask>(_services), 25, 65, progress, failures, cancellationToken).ConfigureAwait(false);
        }

        await RunStepAsync(() => ActivatorUtilities.CreateInstance<SyncCollectionsTask>(_services), 90, 10, progress, failures, cancellationToken).ConfigureAwait(false);

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
        var stepName = "a content step";
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
