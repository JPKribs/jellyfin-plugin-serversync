using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Removes the schedules of this plugin's hidden per sync type tasks, whose stale timers would run them beside Sync Content and Sync Information.
/// The work waits in the background for startup to finish, since Jellyfin registers scheduled tasks after it starts hosted services.
/// </summary>
public sealed class HiddenTaskScheduleCleaner : IHostedService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(10);

    private readonly ITaskManager _taskManager;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<HiddenTaskScheduleCleaner> _logger;
    private CancellationTokenSource? _stopping;
    private Task? _work;

    /// <summary>
    /// Initializes a new instance of the <see cref="HiddenTaskScheduleCleaner"/> class.
    /// </summary>
    /// <param name="taskManager">Reads and changes the scheduled tasks.</param>
    /// <param name="applicationHost">The server host, which says when startup has finished.</param>
    /// <param name="logger">Writes log entries.</param>
    public HiddenTaskScheduleCleaner(ITaskManager taskManager, IServerApplicationHost applicationHost, ILogger<HiddenTaskScheduleCleaner> logger)
    {
        ArgumentNullException.ThrowIfNull(taskManager);
        ArgumentNullException.ThrowIfNull(applicationHost);
        ArgumentNullException.ThrowIfNull(logger);

        _taskManager = taskManager;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _work = Task.Run(() => CleanWhenReadyAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping is not null)
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }

        if (_work is not null)
        {
            try
            {
                await _work.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopping cancels the work.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _stopping?.Dispose();

    /// <summary>Clears the schedule of every hidden task of this plugin that still has one, which Jellyfin saves to the task's file.</summary>
    /// <returns>How many schedules were cleared.</returns>
    public int ClearHiddenSchedules()
    {
        var cleared = 0;
        foreach (var worker in _taskManager.ScheduledTasks)
        {
            var task = worker.ScheduledTask;
            if (task is not IConfigurableScheduledTask { IsHidden: true }
                || task.GetType().Assembly != typeof(HiddenTaskScheduleCleaner).Assembly
                || worker.Triggers.Count == 0)
            {
                continue;
            }

            worker.Triggers = Array.Empty<TaskTriggerInfo>();
            cleared++;
        }

        return cleared;
    }

    /// <summary>Waits for Jellyfin to register the tasks, then clears the hidden schedules.</summary>
    private async Task CleanWhenReadyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + GiveUpAfter;
        while (!_applicationHost.CoreStartupHasCompleted && !OurTasksRegistered())
        {
            if (DateTime.UtcNow > deadline)
            {
                _logger.LogWarning("Jellyfin did not finish starting within {Minutes} minutes, so the hidden per module task schedules were not checked", GiveUpAfter.TotalMinutes);
                return;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var cleared = ClearHiddenSchedules();
            if (cleared > 0)
            {
                _logger.LogInformation("Removed the schedules of {Count} per module task(s). Sync Content and Sync Information run them now", cleared);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not clear the schedules of the hidden per module tasks");
        }
    }

    private bool OurTasksRegistered()
        => _taskManager.ScheduledTasks.Any(w => w.ScheduledTask.GetType().Assembly == typeof(HiddenTaskScheduleCleaner).Assembly);
}
