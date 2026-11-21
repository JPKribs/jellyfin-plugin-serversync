using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Cleanup;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Downloads;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.ContentSync;

/// <summary>
/// Deletes temporary files that failed downloads left behind.
/// </summary>
public class CleanupTempFilesTask : IScheduledTask
{
    private readonly ILogger<CleanupTempFilesTask> _logger;
    private readonly IPluginConfigurationManager _configManager;

    /// <summary>
    /// How many hours a temp file goes unwritten before it counts as left behind.
    /// </summary>
    private const int MaxTempFileAgeHours = 24;

    /// <summary>
    /// The temp subfolders where Metadata and People buffer image downloads.
    /// Only a crash in the middle of an apply leaves a file there, and the same age rule clears it.
    /// </summary>
    private static readonly string[] ImageTempSubdirectories = { "metadata-images", "person-images" };

    /// <summary>Initializes a new instance of the <see cref="CleanupTempFilesTask"/> class.</summary>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="configManager">Supplies the settings and the temp download path.</param>
    public CleanupTempFilesTask(ILogger<CleanupTempFilesTask> logger, IPluginConfigurationManager configManager)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(configManager);

        _logger = logger;
        _configManager = configManager;
    }

    /// <inheritdoc />
    public string Name => "Clean Temporary Directory";

    /// <inheritdoc />
    public string Key => "ServerSyncCleanupTempFiles";

    /// <inheritdoc />
    public string Description => "Deletes leftover files from failed downloads.";

    /// <inheritdoc />
    public string Category => "Server Sync";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var config = _configManager.Configuration;
        // Content downloads and the metadata and people image downloads all stage files here.
        if (!config.EnableContentSync && !config.EnableMetadataSync && !config.EnablePeopleSync)
        {
            return Task.CompletedTask;
        }

        var tempPath = _configManager.GetTempDownloadPath();

        if (!Directory.Exists(tempPath))
        {
            _logger.LogDebug("Temp directory does not exist: {TempPath}", tempPath);
            return Task.CompletedTask;
        }

        // A temp path overlapping a library root would delete media.
        if (RecyclingBinService.OverlapsLibraryRoot(tempPath, config))
        {
            _logger.LogError(
                "Temp download path {Path} overlaps a configured library root, cleanup would delete media files. Skipping cleanup until the path is corrected",
                tempPath);
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
                var files = new List<string>(Directory.GetFiles(tempPath));
                foreach (var imageDir in ImageTempSubdirectories.Select(subdirectory => Path.Combine(tempPath, subdirectory)))
                {
                    if (Directory.Exists(imageDir))
                    {
                        files.AddRange(Directory.GetFiles(imageDir));
                    }
                }

                tempFiles = files.ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Failed to enumerate temp directory: {TempPath}", tempPath);
                return Task.CompletedTask;
            }

            var totalFiles = tempFiles.Length;
            if (totalFiles == 0)
            {
                return Task.CompletedTask;
            }

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

                    // An active download can look stale, and deleting it strands the writer.
                    if (activeTempFiles.Contains(file.ToLowerInvariant()))
                    {
                        _logger.LogDebug("Skipping active download: {FileName}", FormatUtilities.SanitizeForLog(fileInfo.Name));
                        skippedCount++;
                        continue;
                    }

                    if (fileInfo.LastWriteTimeUtc < cutoffTime)
                    {
                        // A download may have started since the snapshot, so it is checked again.
                        if (ActiveDownloadTracker.IsTempFileInUse(file))
                        {
                            _logger.LogDebug("Skipping newly-active download: {FileName}", FormatUtilities.SanitizeForLog(fileInfo.Name));
                            skippedCount++;
                            continue;
                        }

                        var fileSize = fileInfo.Length;
                        fileInfo.Delete();
                        deletedCount++;
                        totalBytes += fileSize;
                        _logger.LogDebug("Deleted orphaned temp file: {FileName}", FormatUtilities.SanitizeForLog(fileInfo.Name));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Failed to delete temp file: {FilePath}", FormatUtilities.SanitizeForLog(file));
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to clean up temp directory: {TempPath}", tempPath);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
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
