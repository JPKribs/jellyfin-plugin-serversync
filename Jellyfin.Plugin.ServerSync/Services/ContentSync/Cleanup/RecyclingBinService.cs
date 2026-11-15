using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.ContentSync.Cleanup;

/// <summary>
/// Moves files to the Recycling Bin and deletes the ones past their retention period.
/// </summary>
public static class RecyclingBinService
{
    /// <summary>
    /// Moves a file to the Recycling Bin under a name that carries the time it was moved.
    /// </summary>
    /// <param name="filePath">The file to move.</param>
    /// <param name="recyclingBinPath">The Recycling Bin folder the file moves into.</param>
    /// <param name="logger">Records the move and any failure.</param>
    /// <returns><c>true</c> when the file was moved.</returns>
    public static bool MoveToRecyclingBin(string filePath, string recyclingBinPath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(recyclingBinPath))
        {
            return false;
        }

        if (!File.Exists(filePath))
        {
            logger.LogWarning("Cannot move to recycling bin - file does not exist: {FilePath}", FormatUtilities.SanitizeForLog(filePath));
            return false;
        }

        try
        {
            if (!Directory.Exists(recyclingBinPath))
            {
                Directory.CreateDirectory(recyclingBinPath);
                logger.LogDebug("Created recycling bin directory: {Path}", FormatUtilities.SanitizeForLog(recyclingBinPath));
            }

            var recycledFileName = FileOperationUtilities.GenerateRecycledFileName(filePath);
            var destinationPath = Path.Combine(recyclingBinPath, recycledFileName);

            if (File.Exists(destinationPath))
            {
                var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
                var ext = Path.GetExtension(recycledFileName);
                var nameWithoutExt = Path.GetFileNameWithoutExtension(recycledFileName);
                recycledFileName = $"{nameWithoutExt}_{uniqueSuffix}{ext}";
                destinationPath = Path.Combine(recyclingBinPath, recycledFileName);
            }

            File.Move(filePath, destinationPath);
            logger.LogDebug("Moved to recycling bin: {FileName} -> {RecycledName}", FormatUtilities.SanitizeForLog(Path.GetFileName(filePath)), FormatUtilities.SanitizeForLog(recycledFileName));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logger.LogError(ex, "Failed to move file to recycling bin: {FilePath}", FormatUtilities.SanitizeForLog(filePath));
            return false;
        }
    }

    /// <summary>
    /// Moves an item's trickplay folder to the Recycling Bin under a name that carries the time, which the retention cleanup reads.
    /// </summary>
    /// <param name="folderPath">The folder to move.</param>
    /// <param name="recyclingBinPath">The Recycling Bin folder the folder moves into.</param>
    /// <param name="logger">Records the move and any failure.</param>
    /// <returns><c>true</c> when the folder was moved.</returns>
    public static bool MoveFolderToRecyclingBin(string folderPath, string recyclingBinPath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

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
            logger.LogDebug("Moved folder to recycling bin: {Folder}", FormatUtilities.SanitizeForLog(Path.GetFileName(folderPath)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to move folder to recycling bin: {Folder}", FormatUtilities.SanitizeForLog(folderPath));
            return false;
        }
    }

    /// <summary>
    /// Moves the backup a download set aside for the file it replaced to the Recycling Bin, named as if it were the original file.
    /// </summary>
    /// <param name="backupPath">Where the backup of the replaced file sits.</param>
    /// <param name="originalDisplayPath">The replaced file's original path, which the recycled name is built from.</param>
    /// <param name="recyclingBinPath">The Recycling Bin folder the backup moves into.</param>
    /// <param name="logger">Records the move and any failure.</param>
    /// <returns><c>true</c> when the backup was moved.</returns>
    public static bool ArchiveBackupToRecyclingBin(string backupPath, string originalDisplayPath, string recyclingBinPath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrEmpty(backupPath) || string.IsNullOrEmpty(originalDisplayPath) || string.IsNullOrEmpty(recyclingBinPath))
        {
            return false;
        }

        if (!File.Exists(backupPath))
        {
            logger.LogWarning("Cannot archive backup - file does not exist: {BackupPath}", FormatUtilities.SanitizeForLog(backupPath));
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
                FormatUtilities.SanitizeForLog(Path.GetFileName(originalDisplayPath)),
                FormatUtilities.SanitizeForLog(recycledFileName));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logger.LogError(ex, "Failed to archive backup to recycling bin: {BackupPath}", FormatUtilities.SanitizeForLog(backupPath));
            return false;
        }
    }

    /// <summary>
    /// Moves a file to the Recycling Bin, then its companion files, local images, and trickplay folder.
    /// Companions come from the strict matcher, so files that only share a name prefix stay.
    /// </summary>
    /// <param name="filePath">The main file to move.</param>
    /// <param name="recyclingBinPath">The Recycling Bin folder everything moves into.</param>
    /// <param name="logger">Records each move and any failure.</param>
    /// <returns><c>true</c> when the main file was moved.</returns>
    public static bool MoveWithCompanionsToRecyclingBin(string filePath, string recyclingBinPath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(recyclingBinPath))
        {
            return false;
        }

        // Moving companions alone would strip a file that is still in use.
        if (!MoveToRecyclingBin(filePath, recyclingBinPath, logger))
        {
            return false;
        }

        try
        {
            foreach (var companionFile in FileOperationUtilities.GetCompanionFiles(filePath))
            {
                MoveToRecyclingBin(companionFile, recyclingBinPath, logger);
            }

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

        // The main file has moved, so a companion failure must not report failure.
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error processing companion files for {FilePath}", FormatUtilities.SanitizeForLog(filePath));
        }

        return true;
    }

    /// <summary>
    /// Checks whether a folder the plugin cleans up on its own, the Recycling Bin or the temp folder, lies inside a root folder or holds one.
    /// Cleaning up such a folder could delete library files for good.
    /// </summary>
    /// <param name="recyclingBinPath">The folder to check.</param>
    /// <param name="config">Supplies the root folder of every library mapping.</param>
    /// <returns><c>true</c> when the folder and a root folder overlap.</returns>
    public static bool OverlapsLibraryRoot(string recyclingBinPath, PluginConfiguration config)
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
            var binFromRoot = Path.GetRelativePath(root, bin);
            var rootFromBin = Path.GetRelativePath(bin, root);
            var binInRoot = !binFromRoot.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(binFromRoot);
            var rootInBin = !rootFromBin.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rootFromBin);
            if (binInRoot || rootInBin)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Deletes the files and trickplay folders the plugin put in the Recycling Bin once the time in their name is past the retention period.
    /// Anything named another way belongs to someone else and is never touched.
    /// </summary>
    /// <param name="recyclingBinPath">The Recycling Bin folder to clean.</param>
    /// <param name="retentionDays">How many days a recycled file is kept before it is deleted.</param>
    /// <param name="logger">Records each deletion and each failure.</param>
    /// <returns>How many files and trickplay folders were deleted.</returns>
    public static int CleanupExpiredFiles(string recyclingBinPath, int retentionDays, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

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
                    // A file the plugin did not name is someone else's and never expires.
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
                        logger.LogDebug("Permanently deleted from recycling bin: {FileName}", FormatUtilities.SanitizeForLog(fileInfo.Name));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Failed to delete recycled file: {FilePath}", FormatUtilities.SanitizeForLog(file));
                    errorCount++;
                }
            }

            foreach (var folder in Directory.GetDirectories(recyclingBinPath))
            {
                // Only recycled trickplay folders expire, and any other folder is someone else's.
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
                    logger.LogDebug("Permanently deleted folder from recycling bin: {Folder}", FormatUtilities.SanitizeForLog(Path.GetFileName(folder)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Failed to delete recycled folder: {FolderPath}", FormatUtilities.SanitizeForLog(folder));
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logger.LogError(ex, "Failed to clean up recycling bin: {Path}", FormatUtilities.SanitizeForLog(recyclingBinPath));
        }

        return deletedCount + deletedFolders;
    }

    /// <summary>
    /// Matches a trickplay folder as <see cref="MoveFolderToRecyclingBin"/> names it, with eight hex digits appended when that name was taken.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex RecycledTrickplayName = new(
        "^.+_(?<at>[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{2}-[0-9]{2}-[0-9]{2})\\.trickplay(_[0-9a-f]{8})?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads the time a trickplay folder moved to the Recycling Bin from its name, for names the plugin built only.
    /// </summary>
    /// <param name="folderName">The folder's name, without its path.</param>
    /// <param name="recycledAt">The UTC time the folder moved to the Recycling Bin.</param>
    /// <returns><c>true</c> when the plugin built the name for a recycled trickplay folder.</returns>
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
