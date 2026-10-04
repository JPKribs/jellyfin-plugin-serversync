using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
public sealed class HiddenTaskScheduleCleaner : IHostedService
{
    private readonly ITaskManager _taskManager;
    private readonly ILogger<HiddenTaskScheduleCleaner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HiddenTaskScheduleCleaner"/> class.
    /// </summary>
    /// <param name="taskManager">The task manager.</param>
    /// <param name="logger">Logger.</param>
    public HiddenTaskScheduleCleaner(ITaskManager taskManager, ILogger<HiddenTaskScheduleCleaner> logger)
    {
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
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

            if (cleared > 0)
            {
                _logger.LogInformation("Removed the schedules of {Count} per module task(s); Sync Content and Sync Information run them now", cleared);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear the schedules of the hidden per module tasks");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
