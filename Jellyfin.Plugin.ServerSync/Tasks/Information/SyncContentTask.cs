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
/// new files over between runs; this task is the safety net that also handles replacements,
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

        var steps = new List<Func<IScheduledTask>>
        {
            () => ActivatorUtilities.CreateInstance<UpdateSyncTablesTask>(_services),
            () => ActivatorUtilities.CreateInstance<SyncMissingContentTask>(_services),
            () => ActivatorUtilities.CreateInstance<SyncCollectionsTask>(_services)
        };

        // The refresh and the download get most of the bar. Collections are quick.
        var shares = new[] { 25.0, 65.0, 10.0 };
        var offset = 0.0;
        for (var i = 0; i < steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = offset;
            var share = shares[i];
            var task = steps[i]();
            _logger.LogInformation("{Task}: running {Step}", Name, task.Name);
            await task.ExecuteAsync(new Progress<double>(p => progress.Report(start + (share * Math.Clamp(p, 0, 100) / 100.0))), cancellationToken).ConfigureAwait(false);
            offset += share;
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
