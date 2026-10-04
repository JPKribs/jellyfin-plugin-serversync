using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Removes the schedules of this plugin's hidden per module tasks. They used to run on their own
/// timers and Jellyfin keeps those timers in its own files across upgrades, so without this an
/// upgraded install would keep running the old tasks beside Sync Content and Sync Information.
/// The tasks stay registered for the dashboard's run buttons.
/// </summary>
/// <remarks>
/// Jellyfin starts hosted services before it registers scheduled tasks, so the task list is empty
/// when <see cref="StartAsync"/> runs. The work waits in the background for startup to finish, which
/// Jellyfin marks only after every task is registered.
/// </remarks>
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
    /// <param name="taskManager">The task manager.</param>
    /// <param name="applicationHost">The server host, which says when startup has finished.</param>
    /// <param name="logger">Logger.</param>
    public HiddenTaskScheduleCleaner(ITaskManager taskManager, IServerApplicationHost applicationHost, ILogger<HiddenTaskScheduleCleaner> logger)
    {
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
                // Expected on stop.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _stopping?.Dispose();

    /// <summary>Clears the schedule of every hidden task of this plugin that still has one.</summary>
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

            // Writes an empty schedule to Jellyfin's file for the task, so it stays cleared.
            worker.Triggers = Array.Empty<TaskTriggerInfo>();
            cleared++;
        }

        return cleared;
    }

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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear the schedules of the hidden per module tasks");
        }
    }

    private bool OurTasksRegistered()
        => _taskManager.ScheduledTasks.Any(w => w.ScheduledTask.GetType().Assembly == typeof(HiddenTaskScheduleCleaner).Assembly);
}
