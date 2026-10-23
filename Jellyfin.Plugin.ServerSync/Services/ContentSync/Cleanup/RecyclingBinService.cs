using System;
using Jellyfin.Plugin.ServerSync.Configuration;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
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
    /// Moves a folder (an item's trickplay) to the recycling bin under a timestamped name, which the
    /// retention cleanup reads to remove it once it expires.
    /// </summary>
    /// <param name="folderPath">The folder to move.</param>
    /// <param name="recyclingBinPath">Path to the recycling bin directory.</param>
    /// <param name="logger">Logger for operation output.</param>
    /// <returns>True when it was moved.</returns>
    public static bool MoveFolderToRecyclingBin(string folderPath, string recyclingBinPath, ILogger logger)
    {
        if (string.IsNullOrEmpty(folderPath) || string.IsNullOrEmpty(recyclingBinPath) || !Directory.Exists(folderPath))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(recyclingBinPath);
            var destination = Path.Combine(recyclingBinPath, FileOperationUtilities.GenerateRecycledFileName(folderPath));
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                destination += "_" + Guid.NewGuid().ToString("N")[..8];
            }

            Directory.Move(folderPath, destination);
            logger.LogDebug("Moved folder to recycling bin: {Folder}", Path.GetFileName(folderPath));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to move folder to recycling bin: {Folder}", folderPath);
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

        // Move the main file first. When it stays put, its companions stay with it, since recycling
        // them alone would strip the nfo, artwork and subtitles from a file that is still in use.
        if (!MoveToRecyclingBin(filePath, recyclingBinPath, logger))
        {
            return false;
        }

        // Move companion files (using the same strict matcher as
        // FileOperationUtilities.GetCompanionFiles so we don't accidentally
        // recycle siblings whose names share a prefix).
        try
        {
            foreach (var companionFile in FileOperationUtilities.GetCompanionFiles(filePath))
            {
                MoveToRecyclingBin(companionFile, recyclingBinPath, logger);
            }

            // Its own thumbnails and trickplay go with it, the same as when it is deleted outright.
            var (images, trickplay) = FileOperationUtilities.GetItemArtifacts(filePath);
            foreach (var image in images)
            {
                MoveToRecyclingBin(image, recyclingBinPath, logger);
            }

            if (trickplay is not null)
            {
                MoveFolderToRecyclingBin(trickplay, recyclingBinPath, logger);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error processing companion files for {FilePath}", filePath);
        }

        return true;
    }

    /// <summary>
    /// Returns true when the recycling bin path overlaps a configured library
    /// root (bin inside a library, or a library inside the bin). Cleanup
    /// falls back to LastWriteTimeUtc for files without a recycled-timestamp
    /// name, so running it against a media folder would permanently delete
    /// ordinary media files older than the retention period.
    /// </summary>
    public static bool OverlapsLibraryRoot(string recyclingBinPath, Jellyfin.Plugin.ServerSync.Configuration.PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrEmpty(recyclingBinPath))
        {
            return false;
        }

        var bin = Path.GetFullPath(recyclingBinPath);
        foreach (var mapping in config.GetAllLibraryMappings())
        {
            if (string.IsNullOrEmpty(mapping.LocalRootPath))
            {
                continue;
            }

            var root = Path.GetFullPath(mapping.LocalRootPath);
            var binInRoot = !Path.GetRelativePath(root, bin).StartsWith("..", StringComparison.Ordinal)
                && !Path.IsPathRooted(Path.GetRelativePath(root, bin));
            var rootInBin = !Path.GetRelativePath(bin, root).StartsWith("..", StringComparison.Ordinal)
                && !Path.IsPathRooted(Path.GetRelativePath(bin, root));
            if (binInRoot || rootInBin)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Deletes the files and recycled trickplay folders the plugin put in the recycling bin once they are
    /// older than the retention period. Anything else in the folder is left alone.
    /// </summary>
    /// <param name="recyclingBinPath">Path to the recycling bin directory.</param>
    /// <param name="retentionDays">Number of days to retain files before deletion.</param>
    /// <param name="logger">Logger for operation output.</param>
    /// <returns>Number of files and recycled trickplay folders deleted.</returns>
    public static int CleanupExpiredFiles(string recyclingBinPath, int retentionDays, ILogger logger)
    {
        if (string.IsNullOrEmpty(recyclingBinPath) || !Directory.Exists(recyclingBinPath))
        {
            return 0;
        }

        var cutoffTime = DateTime.UtcNow.AddDays(-retentionDays);
        var deletedCount = 0;
        var deletedFolders = 0;
        var errorCount = 0;
        long totalBytes = 0;

        try
        {
            var files = Directory.GetFiles(recyclingBinPath);

            foreach (var file in files)
            {
                try
                {
                    // Only files the bin named expire, by the time in their name. A file named any
                    // other way was put here by someone else and is never touched, however old.
                    if (!FileOperationUtilities.TryReadRecycledTime(file, out var fileTime))
                    {
                        continue;
                    }

                    var fileInfo = new FileInfo(file);
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

            // The only folders the plugin puts here are recycled trickplay folders, and only those expire,
            // by the timestamp in their name. Any other folder, even one named with a timestamp, belongs
            // to someone else and is never touched.
            foreach (var folder in Directory.GetDirectories(recyclingBinPath))
            {
                if (!TryReadRecycledTrickplayTime(Path.GetFileName(folder), out var folderTime) || folderTime >= cutoffTime)
                {
                    continue;
                }

                try
                {
                    var folderSize = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                    Directory.Delete(folder, recursive: true);
                    deletedFolders++;
                    totalBytes += folderSize;
                    logger.LogDebug("Permanently deleted folder from recycling bin: {Folder}", Path.GetFileName(folder));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete recycled folder: {FolderPath}", folder);
                    errorCount++;
                }
            }

            if (deletedCount > 0 || deletedFolders > 0)
            {
                logger.LogInformation(
                    "Recycling bin cleanup: permanently deleted {Count} files and {Folders} folders ({Size})",
                    deletedCount,
                    deletedFolders,
                    FormatUtilities.FormatBytes(totalBytes));
            }

            if (errorCount > 0)
            {
                logger.LogWarning("Failed to delete {Count} items from recycling bin", errorCount);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clean up recycling bin: {Path}", recyclingBinPath);
        }

        return deletedCount + deletedFolders;
    }

    // A recycled trickplay folder as MoveFolderToRecyclingBin names it: the recycled name of
    // "<name>.trickplay", "<path>.<name>_yyyy-MM-dd_HH-mm-ss.trickplay", with "_" and eight hex digits
    // appended when that name was taken.
    private static readonly System.Text.RegularExpressions.Regex RecycledTrickplayName = new(
        "^.+_(?<at>[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{2}-[0-9]{2}-[0-9]{2})\\.trickplay(_[0-9a-f]{8})?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads the time a recycled trickplay folder was recycled at from its name. Only names the plugin
    /// builds for those folders qualify.
    /// </summary>
    /// <param name="folderName">The folder's name.</param>
    /// <param name="recycledAt">The UTC time it was recycled at.</param>
    /// <returns>True when the name is one of the plugin's recycled trickplay folders.</returns>
    public static bool TryReadRecycledTrickplayTime(string folderName, out DateTime recycledAt)
    {
        recycledAt = default;
        var match = RecycledTrickplayName.Match(folderName ?? string.Empty);
        return match.Success && DateTime.TryParseExact(
            match.Groups["at"].Value,
            "yyyy-MM-dd_HH-mm-ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out recycledAt);
    }
}
