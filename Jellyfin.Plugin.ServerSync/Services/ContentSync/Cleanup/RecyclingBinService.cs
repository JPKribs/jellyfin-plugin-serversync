using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Utilities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Handles moving files to the recycling bin and cleaning up old files.
/// </summary>
public static class RecyclingBinService
{
    /// <summary>
    /// Moves a file to the recycling bin with a timestamped name.
    /// </summary>
    /// <param name="filePath">Path to the file to move.</param>
    /// <param name="recyclingBinPath">Path to the recycling bin directory.</param>
    /// <param name="logger">Logger for operation output.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public static bool MoveToRecyclingBin(string filePath, string recyclingBinPath, ILogger logger)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(recyclingBinPath))
        {
            return false;
        }

        if (!File.Exists(filePath))
        {
            logger.LogWarning("Cannot move to recycling bin - file does not exist: {FilePath}", filePath);
            return false;
        }

        try
        {
            // Ensure recycling bin directory exists
            if (!Directory.Exists(recyclingBinPath))
            {
                Directory.CreateDirectory(recyclingBinPath);
                logger.LogDebug("Created recycling bin directory: {Path}", recyclingBinPath);
            }

            // Generate recycled file name: path.with.periods_2026-01-29_17-30-45.ext
            var recycledFileName = FileOperationUtilities.GenerateRecycledFileName(filePath);
            var destinationPath = Path.Combine(recyclingBinPath, recycledFileName);

            // Handle case where destination already exists (unlikely but possible)
            if (File.Exists(destinationPath))
            {
                var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
                var ext = Path.GetExtension(recycledFileName);
                var nameWithoutExt = Path.GetFileNameWithoutExtension(recycledFileName);
                recycledFileName = $"{nameWithoutExt}_{uniqueSuffix}{ext}";
                destinationPath = Path.Combine(recyclingBinPath, recycledFileName);
            }

            File.Move(filePath, destinationPath);
            logger.LogDebug("Moved to recycling bin: {FileName} -> {RecycledName}", Path.GetFileName(filePath), recycledFileName);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to move file to recycling bin: {FilePath}", filePath);
            return false;
        }
    }

    /// <summary>
    /// Moves a sidecar/backup file to the recycling bin, naming it as if it
    /// were the original file. Used by the download pipeline after a successful
    /// atomic rename: the previous version was first moved to a sidecar (so it
    /// could be restored on rename failure), and is now archived under its
    /// original path's recycled filename.
    /// </summary>
    /// <param name="backupPath">Current location of the sidecar to archive.</param>
    /// <param name="originalDisplayPath">Original path of the file (used to derive the recycled filename).</param>
    /// <param name="recyclingBinPath">Path to the recycling bin directory.</param>
    /// <param name="logger">Logger for operation output.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public static bool ArchiveBackupToRecyclingBin(string backupPath, string originalDisplayPath, string recyclingBinPath, ILogger logger)
    {
        if (string.IsNullOrEmpty(backupPath) || string.IsNullOrEmpty(originalDisplayPath) || string.IsNullOrEmpty(recyclingBinPath))
        {
            return false;
        }

        if (!File.Exists(backupPath))
        {
            logger.LogWarning("Cannot archive backup - file does not exist: {BackupPath}", backupPath);
            return false;
        }

        try
        {
            if (!Directory.Exists(recyclingBinPath))
            {
                Directory.CreateDirectory(recyclingBinPath);
            }

            var recycledFileName = FileOperationUtilities.GenerateRecycledFileName(originalDisplayPath);
            var destinationPath = Path.Combine(recyclingBinPath, recycledFileName);

            if (File.Exists(destinationPath))
            {
                var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
                var ext = Path.GetExtension(recycledFileName);
                var nameWithoutExt = Path.GetFileNameWithoutExtension(recycledFileName);
                recycledFileName = $"{nameWithoutExt}_{uniqueSuffix}{ext}";
                destinationPath = Path.Combine(recyclingBinPath, recycledFileName);
            }

            File.Move(backupPath, destinationPath);
            logger.LogDebug(
                "Archived previous version to recycling bin: {OriginalName} -> {RecycledName}",
                Path.GetFileName(originalDisplayPath),
                recycledFileName);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to archive backup to recycling bin: {BackupPath}", backupPath);
            return false;
        }
    }

    /// <summary>
    /// Moves a file and its companion files (subtitles, etc.) to the recycling bin.
    /// </summary>
    /// <param name="filePath">Path to the main file to move.</param>
    /// <param name="recyclingBinPath">Path to the recycling bin directory.</param>
    /// <param name="logger">Logger for operation output.</param>
    /// <returns>True if the main file was moved successfully.</returns>
    public static bool MoveWithCompanionsToRecyclingBin(string filePath, string recyclingBinPath, ILogger logger)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(recyclingBinPath))
        {
            return false;
        }

        // Move the main file first
        var mainSuccess = MoveToRecyclingBin(filePath, recyclingBinPath, logger);

        // Move companion files (using the same strict matcher as
        // FileOperationUtilities.GetCompanionFiles so we don't accidentally
        // recycle siblings whose names share a prefix).
        try
        {
            foreach (var companionFile in FileOperationUtilities.GetCompanionFiles(filePath))
            {
                MoveToRecyclingBin(companionFile, recyclingBinPath, logger);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error processing companion files for {FilePath}", filePath);
        }

        return mainSuccess;
    }

    /// <summary>
    /// Deletes files in the recycling bin older than the retention period.
    /// </summary>
    /// <param name="recyclingBinPath">Path to the recycling bin directory.</param>
    /// <param name="retentionDays">Number of days to retain files before deletion.</param>
    /// <param name="logger">Logger for operation output.</param>
    /// <returns>Number of files deleted.</returns>
    public static int CleanupExpiredFiles(string recyclingBinPath, int retentionDays, ILogger logger)
    {
        if (string.IsNullOrEmpty(recyclingBinPath) || !Directory.Exists(recyclingBinPath))
        {
            return 0;
        }

        var cutoffTime = DateTime.UtcNow.AddDays(-retentionDays);
        var deletedCount = 0;
        var errorCount = 0;
        long totalBytes = 0;

        try
        {
            var files = Directory.GetFiles(recyclingBinPath);

            foreach (var file in files)
            {
                try
                {
                    var fileInfo = new FileInfo(file);

                    // Check if file is older than retention period
                    // Use the timestamp from the filename if possible, otherwise use file modification time
                    var fileTime = FileOperationUtilities.ExtractTimestampFromFileName(file) ?? fileInfo.LastWriteTimeUtc;

                    if (fileTime < cutoffTime)
                    {
                        var fileSize = fileInfo.Length;
                        fileInfo.Delete();
                        deletedCount++;
                        totalBytes += fileSize;
                        logger.LogDebug("Permanently deleted from recycling bin: {FileName}", fileInfo.Name);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete recycled file: {FilePath}", file);
                    errorCount++;
                }
            }

            if (deletedCount > 0)
            {
                logger.LogInformation(
                    "Recycling bin cleanup: permanently deleted {Count} files ({Size})",
                    deletedCount,
                    FormatUtilities.FormatBytes(totalBytes));
            }

            if (errorCount > 0)
            {
                logger.LogWarning("Failed to delete {Count} files from recycling bin", errorCount);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clean up recycling bin: {Path}", recyclingBinPath);
        }

        return deletedCount;
    }
}
