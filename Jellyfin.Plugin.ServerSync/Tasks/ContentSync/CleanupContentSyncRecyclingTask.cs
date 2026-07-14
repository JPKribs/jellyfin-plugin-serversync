using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Scheduled task to permanently delete expired files from the recycling bin.
/// </summary>
public class EmptyRecyclingBinTask : IScheduledTask
{
    private readonly ILogger<EmptyRecyclingBinTask> _logger;
    private readonly IPluginConfigurationManager _configManager;

    public EmptyRecyclingBinTask(ILogger<EmptyRecyclingBinTask> logger, IPluginConfigurationManager configManager)
    {
        _logger = logger;
        _configManager = configManager;
    }

    public string Name => "Empty Recycling Bin";

    public string Key => "ServerSyncEmptyRecyclingBin";

    public string Description => "Permanently deletes files that have been in the recycling bin longer than the configured retention period.";

    public string Category => "Content Sync";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = _configManager.Configuration;

        // Skip if content sync is not enabled
        if (!config.EnableContentSync)
        {
            return Task.CompletedTask;
        }

        // Skip if recycling bin is not enabled
        if (!config.EnableRecyclingBin)
        {
            _logger.LogDebug("Recycling bin is not enabled, skipping cleanup");
            return Task.CompletedTask;
        }

        if (string.IsNullOrEmpty(config.RecyclingBinPath))
        {
            _logger.LogWarning("Recycling bin path is not configured");
            return Task.CompletedTask;
        }

        if (RecyclingBinService.OverlapsLibraryRoot(config.RecyclingBinPath, config))
        {
            _logger.LogError(
                "Recycling bin path {Path} overlaps a configured library root — cleanup would permanently delete media files. Skipping cleanup until the path is corrected.",
                config.RecyclingBinPath);
            return Task.CompletedTask;
        }

        progress.Report(0);

        var deletedCount = RecyclingBinService.CleanupExpiredFiles(
            config.RecyclingBinPath,
            config.RecyclingBinRetentionDays,
            _logger);

        progress.Report(100);

        if (deletedCount == 0)
        {
            _logger.LogDebug("No expired files found in recycling bin");
        }

        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromDays(1).Ticks
            }
        };
    }
}
