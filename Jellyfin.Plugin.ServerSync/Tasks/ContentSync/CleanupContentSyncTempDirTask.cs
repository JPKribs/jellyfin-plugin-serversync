using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// CleanupTempFilesTask
/// Scheduled task to clean up orphaned temporary files from failed downloads.
/// </summary>
public class CleanupTempFilesTask : IScheduledTask
{
    private readonly ILogger<CleanupTempFilesTask> _logger;
    private readonly IPluginConfigurationManager _configManager;

    /// <summary>
    /// Maximum age in hours before a temp file is considered orphaned.
    /// </summary>
    private const int MaxTempFileAgeHours = 24;

    public CleanupTempFilesTask(ILogger<CleanupTempFilesTask> logger, IPluginConfigurationManager configManager)
    {
        _logger = logger;
        _configManager = configManager;
    }

    public string Name => "Clean Temporary Directory";

    public string Key => "ServerSyncCleanupTempFiles";

    public string Description => "Removes orphaned temporary files from failed sync downloads.";

    public string Category => "Content Sync";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = _configManager.Configuration;
        if (!config.EnableContentSync)
        {
            return Task.CompletedTask;
        }

        var tempPath = _configManager.GetTempDownloadPath();

        if (!Directory.Exists(tempPath))
        {
            _logger.LogDebug("Temp directory does not exist: {TempPath}", tempPath);
            return Task.CompletedTask;
        }

        try
        {
            var cutoffTime = DateTime.UtcNow.AddHours(-MaxTempFileAgeHours);
            var deletedCount = 0;
            var skippedCount = 0;
            var errorCount = 0;
            var totalBytes = 0L;

            string[] tempFiles;
            try
            {
                tempFiles = Directory.GetFiles(tempPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enumerate temp directory: {TempPath}", tempPath);
                return Task.CompletedTask;
            }

            var totalFiles = tempFiles.Length;
            if (totalFiles == 0)
            {
                return Task.CompletedTask;
            }

            // Get list of temp files currently being used by active downloads
            var activeTempFiles = ActiveDownloadTracker.GetActiveTempFiles()
                .Select(f => f.ToLowerInvariant())
                .ToHashSet();

            if (activeTempFiles.Count > 0)
            {
                _logger.LogDebug("Protecting {Count} active download temp files from cleanup", activeTempFiles.Count);
            }

            for (var i = 0; i < tempFiles.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var file = tempFiles[i];

                try
                {
                    var fileInfo = new FileInfo(file);

                    // Never delete a file that an active worker is still
                    // writing to, regardless of age. A slow throttled download
                    // (e.g. 100 KB/s on a 50 GB file) can run for days, but
                    // its on-disk LastWriteTimeUtc only advances when bytes
                    // are flushed — long throttle sleeps make a healthy file
                    // look stale. On Linux/macOS, deleting an open file
                    // succeeds (unlink while open) and silently strands the
                    // writer with a handle pointing to nothing visible.
                    var isActiveDownload = activeTempFiles.Contains(file.ToLowerInvariant());
                    if (isActiveDownload)
                    {
                        _logger.LogDebug("Skipping active download: {FileName}", fileInfo.Name);
                        skippedCount++;
                        continue;
                    }

                    if (fileInfo.LastWriteTimeUtc < cutoffTime)
                    {
                        var fileSize = fileInfo.Length;
                        fileInfo.Delete();
                        deletedCount++;
                        totalBytes += fileSize;
                        _logger.LogDebug("Deleted orphaned temp file: {FileName}", fileInfo.Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temp file: {FilePath}", file);
                    errorCount++;
                }

                progress.Report((double)(i + 1) / totalFiles * 100);
            }

            if (deletedCount > 0 || skippedCount > 0)
            {
                _logger.LogInformation(
                    "Cleanup complete: deleted {DeletedCount} orphaned temp files ({Size}), skipped {SkippedCount} active downloads",
                    deletedCount,
                    FormatUtilities.FormatBytes(totalBytes),
                    skippedCount);
            }

            if (errorCount > 0)
            {
                _logger.LogWarning("Failed to delete {Count} temp files", errorCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clean up temp directory: {TempPath}", tempPath);
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
