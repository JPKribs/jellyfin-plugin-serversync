using System;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Centralized service for file deletion operations including companion files.
/// </summary>
public static class FileDeletionService
{
    private static readonly string[] TitleImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".tbn" };

    // Artwork Jellyfin and other tools save for a title folder as a whole.
    private static readonly Regex TitleImageName = new(
        "^(poster|folder|fanart|backdrop|banner|logo|clearart|thumb|landscape|disc)[0-9]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Text companions of the deleted item that may be left behind ("name.en.srt", "name.txt").
    private static readonly string[] ItemTextExtensions = { ".srt", ".sub", ".idx", ".ass", ".ssa", ".vtt", ".smi", ".txt", ".nfo" };

    /// <summary>
    /// Deletes a file and its companion files permanently.
    /// </summary>
    /// <param name="filePath">Path to the main file to delete.</param>
    /// <param name="logger">Logger for operation output.</param>
    /// <param name="removeEmptyFolders">Whether to remove parent folders if they become empty.</param>
    /// <returns>True if main file was deleted successfully.</returns>
    public static bool DeleteWithCompanions(string filePath, ILogger logger, bool removeEmptyFolders = false, string? libraryRootPath = null)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        var mainDeleted = false;
        var parentDirectory = Path.GetDirectoryName(filePath);

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                mainDeleted = true;
                logger.LogDebug("Deleted file: {FileName}", Path.GetFileName(filePath));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete file: {FilePath}", filePath);
            return false;
        }

        // Delete companion files
        DeleteCompanionFiles(filePath, logger);
        DeleteItemArtifacts(filePath, logger);

        // Remove empty parent folders if enabled, bounded by library root. A folder this item leaves
        // holding nothing but title artwork and nfo files counts as empty, so those go first.
        if (removeEmptyFolders && mainDeleted && !string.IsNullOrEmpty(parentDirectory))
        {
            DeleteTitleArtifacts(parentDirectory, Path.GetFileNameWithoutExtension(filePath), logger, libraryRootPath);
            TryRemoveEmptyFolders(parentDirectory, logger, libraryRootPath);
        }

        return mainDeleted;
    }

    /// <summary>
    /// Attempts to remove empty parent folders after file deletion.
    /// Only removes folders that are completely empty.
    /// Stops at the library root to prevent deleting folders outside the library boundary.
    /// </summary>
    /// <param name="directoryPath">Starting directory to check.</param>
    /// <param name="logger">Logger for operation output.</param>
    /// <param name="libraryRootPath">Optional library root path to prevent walking above the library boundary.</param>
    public static void TryRemoveEmptyFolders(string directoryPath, ILogger logger, string? libraryRootPath = null)
    {
        // Without a known root the walk-up has no boundary and could climb
        // through the library root into mount-point parents, skip cleanup
        // rather than walk unbounded.
        if (string.IsNullOrEmpty(libraryRootPath))
        {
            logger.LogDebug("Skipping empty-folder cleanup for {Directory}: no library root resolved to bound the walk", directoryPath);
            return;
        }

        try
        {
            // Normalize the boundary path for comparison
            var normalizedRoot = Path.GetFullPath(libraryRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Walk up the directory tree and remove empty folders
            var currentDir = directoryPath;

            while (!string.IsNullOrEmpty(currentDir) && Directory.Exists(currentDir))
            {
                // Stop if we've reached or gone above the library root
                var normalizedCurrent = Path.GetFullPath(currentDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(normalizedCurrent, normalizedRoot, StringComparison.OrdinalIgnoreCase)
                    || !normalizedCurrent.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                // Check if directory is empty (no files and no subdirectories)
                var hasFiles = Directory.EnumerateFiles(currentDir).Any();
                var hasSubDirs = Directory.EnumerateDirectories(currentDir).Any();

                if (hasFiles || hasSubDirs)
                {
                    // Directory is not empty, stop here
                    break;
                }

                // Directory is empty, try to delete it
                try
                {
                    Directory.Delete(currentDir, recursive: false);
                    logger.LogDebug("Removed empty folder: {FolderPath}", currentDir);

                    // Move up to parent directory
                    currentDir = Path.GetDirectoryName(currentDir);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not remove folder (may be in use): {FolderPath}", currentDir);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error checking empty folders for {DirectoryPath}", directoryPath);
        }
    }

    /// <summary>
    /// Deletes companion files (subtitles, metadata, images) that match the main file name.
    /// </summary>
    /// <param name="mainFilePath">Path to the main file (companions are found by pattern matching).</param>
    /// <param name="logger">Logger for operation output.</param>
    public static void DeleteCompanionFiles(string mainFilePath, ILogger logger)
    {
        var companions = FileOperationUtilities.GetCompanionFiles(mainFilePath);
        foreach (var companionFile in companions)
        {
            try
            {
                File.Delete(companionFile);
                logger.LogDebug("Deleted companion file: {CompanionFile}", Path.GetFileName(companionFile));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete companion file: {CompanionFile}", Path.GetFileName(companionFile));
            }
        }
    }

    /// <summary>
    /// Deletes what Jellyfin generates for one media file and names after it: its local images
    /// ("name-thumb.jpg") and its trickplay folder ("name.trickplay"). Another version in the same
    /// folder ("name - 4K.mkv") keeps its own.
    /// </summary>
    /// <param name="mainFilePath">Path to the main file.</param>
    /// <param name="logger">Logger for operation output.</param>
    public static void DeleteItemArtifacts(string mainFilePath, ILogger logger)
    {
        var (images, trickplay) = FileOperationUtilities.GetItemArtifacts(mainFilePath);
        foreach (var image in images)
        {
            TryDeleteFile(image, logger);
        }

        if (trickplay is null)
        {
            return;
        }

        try
        {
            Directory.Delete(trickplay, recursive: true);
            logger.LogDebug("Deleted trickplay folder: {Folder}", Path.GetFileName(trickplay));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to delete trickplay folder: {Folder}", trickplay);
        }
    }

    /// <summary>
    /// Deletes the title artwork and nfo files a folder is left with, once the item deleted from it
    /// was the last thing there. Anything else in the folder, a file that is not plainly title
    /// artwork, an nfo, or a text companion of the deleted item, or any subfolder other than the
    /// item's own trickplay, means the folder still holds something, and then nothing is deleted. That
    /// way an unlisted media type, a disc structure, a photo, or a user's own file always keeps the
    /// folder and its artwork. Only a folder strictly inside a known library root is considered.
    /// </summary>
    /// <param name="folder">The folder the deleted file was in.</param>
    /// <param name="deletedStem">The deleted file's name without its extension.</param>
    /// <param name="logger">Logger for operation output.</param>
    /// <param name="libraryRootPath">The library root bounding the cleanup, or null to skip it.</param>
    public static void DeleteTitleArtifacts(string folder, string deletedStem, ILogger logger, string? libraryRootPath)
    {
        if (string.IsNullOrEmpty(libraryRootPath) || string.IsNullOrEmpty(deletedStem) || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            var root = Path.GetFullPath(libraryRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var ownTrickplay = deletedStem + ".trickplay";
            if (Directory.EnumerateDirectories(folder).Any(d => !string.Equals(Path.GetFileName(d), ownTrickplay, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var files = Directory.EnumerateFiles(folder).ToList();
            if (!files.All(f => IsTitleFile(f, deletedStem)))
            {
                return;
            }

            foreach (var file in files)
            {
                TryDeleteFile(file, logger);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not clean the title files in {Folder}", folder);
        }
    }

    private static bool IsTitleFile(string file, string deletedStem)
    {
        var name = Path.GetFileName(file);
        var extension = Path.GetExtension(file);
        if (string.Equals(extension, ".nfo", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TitleImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            && TitleImageName.IsMatch(Path.GetFileNameWithoutExtension(file)))
        {
            return true;
        }

        return name.StartsWith(deletedStem + ".", StringComparison.OrdinalIgnoreCase)
            && ItemTextExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string file, ILogger logger)
    {
        try
        {
            File.Delete(file);
            logger.LogDebug("Deleted file: {FileName}", Path.GetFileName(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to delete file: {FileName}", Path.GetFileName(file));
        }
    }

    /// <summary>
    /// Deletes a file using the recycling bin (moves file instead of permanent delete).
    /// </summary>
    /// <param name="filePath">Path to the file to recycle.</param>
    /// <param name="recyclingBinPath">Path to the recycling bin directory.</param>
    /// <param name="logger">Logger for operation output.</param>
    /// <returns>Deletion result.</returns>
    public static DeletionResult DeleteWithRecyclingBin(string filePath, string recyclingBinPath, ILogger logger)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return new DeletionResult(false, "File path is empty");
        }

        if (string.IsNullOrEmpty(recyclingBinPath))
        {
            return new DeletionResult(false, "Recycling bin path is empty");
        }

        var success = RecyclingBinService.MoveWithCompanionsToRecyclingBin(filePath, recyclingBinPath, logger);
        return success
            ? new DeletionResult(true)
            : new DeletionResult(false, "Failed to move file to recycling bin");
    }

    /// <summary>
    /// Deletes a local item via the Jellyfin library manager API.
    /// This properly handles library database cleanup and companion files.
    /// </summary>
    /// <param name="localItem">The Jellyfin library item to delete.</param>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    /// <param name="deleteFileLocation">Whether to delete the actual file from disk.</param>
    /// <param name="logger">Logger for operation output.</param>
    /// <returns>Deletion result.</returns>
    public static DeletionResult DeleteViaJellyfinApi(
        BaseItem localItem,
        ILibraryManager libraryManager,
        bool deleteFileLocation,
        ILogger logger)
    {
        try
        {
            libraryManager.DeleteItem(
                localItem,
                new DeleteOptions { DeleteFileLocation = deleteFileLocation },
                localItem.GetParent(),
                notifyParentItem: true);

            return new DeletionResult(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete item via Jellyfin API: {ItemName}", localItem.Name);
            return new DeletionResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Deletes a local file using the appropriate strategy based on configuration.
    /// Handles: Jellyfin API deletion, recycling bin, and permanent deletion.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <param name="localItem">Optional Jellyfin library item (null if not in library).</param>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="logger">Logger for operation output.</param>
    /// <returns>Deletion result.</returns>
    public static DeletionResult DeleteLocalFile(
        string? filePath,
        BaseItem? localItem,
        ILibraryManager libraryManager,
        PluginConfiguration config,
        ILogger logger)
    {
        var useRecyclingBin = config.EnableRecyclingBin && !string.IsNullOrEmpty(config.RecyclingBinPath);

        // If item exists in Jellyfin library
        if (localItem != null)
        {
            // The file is deleted here and the item is then removed from Jellyfin without touching the
            // disk. Jellyfin's own file deletion removes the whole containing folder, recursively, for a
            // video that is not in a mixed folder (a movie in its own folder), which takes every other
            // file in that folder with it. The automatic deletion path deletes the same way.
            var path = !string.IsNullOrEmpty(filePath) ? filePath : localItem.Path;

            // A single file item whose file is already gone leaves only a stale entry in Jellyfin. It is
            // removed without touching the disk, and reported as deleted elsewhere.
            if (!string.IsNullOrEmpty(path) && !File.Exists(path) && !Directory.Exists(path) && IsSingleFileItem(localItem))
            {
                var removed = DeleteViaJellyfinApi(localItem, libraryManager, deleteFileLocation: false, logger);
                return removed.Success ? new DeletionResult(true, AlreadyGone: true) : removed;
            }

            var refusal = RefuseLibraryItemDeletion(path, localItem, config);
            if (refusal is not null)
            {
                return new DeletionResult(false, refusal);
            }

            if (useRecyclingBin)
            {
                // Move to recycling bin first, then remove from Jellyfin without deleting file
                var recycleResult = DeleteWithRecyclingBin(path!, config.RecyclingBinPath!, logger);
                if (!recycleResult.Success)
                {
                    return recycleResult;
                }
            }
            else if (!DeleteWithCompanions(path!, logger, config.RemoveEmptyFoldersOnDelete, FileValidationService.GetContainingLibraryRoot(path, config)))
            {
                return new DeletionResult(false, "Failed to delete file");
            }

            // Remove from Jellyfin database only (file already gone)
            return DeleteViaJellyfinApi(localItem, libraryManager, deleteFileLocation: false, logger);
        }

        // File not in Jellyfin library but exists on disk
        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            if (useRecyclingBin)
            {
                return DeleteWithRecyclingBin(filePath, config.RecyclingBinPath!, logger);
            }
            else
            {
                var success = DeleteWithCompanions(filePath, logger);
                return success
                    ? new DeletionResult(true)
                    : new DeletionResult(false, "Failed to delete file");
            }
        }

        // File doesn't exist
        return new DeletionResult(true);
    }

    /// <summary>
    /// Whether an item is one plain file: not a disc structure (DVD, Blu-ray, HD DVD folder) and not
    /// split into stacked parts. Anything that is not a video is one file.
    /// </summary>
    /// <param name="localItem">The Jellyfin item.</param>
    /// <returns>True when the item is a single file.</returns>
    public static bool IsSingleFileItem(BaseItem localItem)
        => localItem is not Video video
            || (!video.IsStacked && video.VideoType is MediaBrowser.Model.Entities.VideoType.VideoFile or MediaBrowser.Model.Entities.VideoType.Iso);

    /// <summary>
    /// Why a library item cannot be deleted as a single file, or null when it can. Server Sync deletes
    /// one file and what belongs to it. A disc structure (a folder) or a stacked multi part item has
    /// more files than that, and a file that is not on disk has nothing to delete, so each is refused
    /// with nothing touched and the row and the Jellyfin item stay as they were. The path that would be
    /// deleted, which may be the item's own path when the row tracks none, must also lie inside a
    /// library root without a symlinked folder on the way.
    /// </summary>
    /// <param name="path">The path that would be deleted.</param>
    /// <param name="localItem">The Jellyfin item.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>The reason, or null.</returns>
    public static string? RefuseLibraryItemDeletion(string? path, BaseItem localItem, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(localItem);
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrEmpty(path))
        {
            return "Deletion refused: the item has no path";
        }

        if (Directory.Exists(path))
        {
            return "Deletion refused: the item is a folder (a DVD or Blu-ray structure), and only single files are deleted here. Delete it from Jellyfin instead";
        }

        if (localItem is Video { IsStacked: true } stacked)
        {
            return $"Deletion refused: the item is split into {stacked.AdditionalParts.Length + 1} parts, and only single files are deleted here. Delete it from Jellyfin instead";
        }

        if (!IsSingleFileItem(localItem))
        {
            return "Deletion refused: the item is a disc structure, and only single files are deleted here. Delete it from Jellyfin instead";
        }

        if (!File.Exists(path))
        {
            return "Deletion refused: the file is not on disk, so nothing was deleted";
        }

        var root = FileValidationService.GetContainingLibraryRoot(path, config);
        if (root is null)
        {
            return "Deletion refused: path is outside all configured library roots";
        }

        if (FileValidationService.HasSymlinkedDirectoryComponent(path, root))
        {
            return "Deletion refused: path traverses a symlinked directory";
        }

        return null;
    }

    /// <summary>
    /// Processes all items marked for deletion in the sync database.
    /// Uses batch operations for database consistency.
    /// </summary>
    /// <param name="manager">Sync table manager.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="logger">Logger for operation output.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tuple of (deleted count, failed count).</returns>
    public static (int Deleted, int Failed) ProcessPendingDeletions(
        ContentSyncTableManager manager,
        PluginConfiguration config,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        if (config.EffectiveMissingContentAction != MissingContentAction.Delete)
        {
            return (0, 0);
        }

        var itemsToDelete = manager.GetByStatus(SyncStatus.Deleting).ToList();

        if (itemsToDelete.Count == 0)
        {
            return (0, 0);
        }

        logger.LogInformation("Processing {Count} items marked for deletion", itemsToDelete.Count);

        // Build lookup for library root paths to bound empty folder cleanup
        var libraryRootLookup = config.GetAllLibraryMappings()
            .Where(m => !string.IsNullOrEmpty(m.SourceLibraryId) && !string.IsNullOrEmpty(m.LocalRootPath))
            .GroupBy(m => m.SourceLibraryId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().LocalRootPath, StringComparer.OrdinalIgnoreCase);

        var deleted = 0;
        var failed = 0;
        var failedItems = new List<(string SourceItemId, string Error)>();

        // Per-item atomicity: file delete → immediate DB row delete. The
        // previous design ran all file deletes first, then a single
        // BatchDelete at the end, if BatchDelete threw (SQLite locked,
        // disk full), the files were gone but the DB still referenced them.
        // A subsequent refresh would see "file missing" + Deleting status
        // and confuse the user with a fake error. Per-item ordering means
        // a transient DB failure leaves the row in place. The next refresh
        // re-marks Deleting and we retry the whole pair on the next sync.
        foreach (var item in itemsToDelete)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var localPath = item.LocalPath;
            var fileName = Path.GetFileName(localPath ?? item.SourcePath);
            var fileGone = false;

            if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
            {
                // Same boundary check the manual deletion endpoint enforces:
                // the unattended path must never delete a file outside every
                // configured library root, no matter what the row says.
                var containingRoot = FileValidationService.GetContainingLibraryRoot(localPath, config);
                if (containingRoot == null)
                {
                    failedItems.Add((item.SourceItemId, "Deletion refused: path is outside all configured library roots"));
                    logger.LogError("Refused to delete {FileName}: {Path} is outside all configured library roots", fileName, localPath);
                    continue;
                }

                if (FileValidationService.HasSymlinkedDirectoryComponent(localPath, containingRoot))
                {
                    failedItems.Add((item.SourceItemId, "Deletion refused: path traverses a symlinked directory"));
                    logger.LogError("Refused to delete {FileName}: {Path} traverses a symlinked directory, so the physical file may live outside the library", fileName, localPath);
                    continue;
                }

                DeletionResult result;
                if (config.EnableRecyclingBin && !string.IsNullOrEmpty(config.RecyclingBinPath))
                {
                    result = DeleteWithRecyclingBin(localPath, config.RecyclingBinPath, logger);
                }
                else
                {
                    libraryRootLookup.TryGetValue(item.SourceLibraryId, out var libraryRoot);
                    result = new DeletionResult(DeleteWithCompanions(localPath, logger, config.RemoveEmptyFoldersOnDelete, libraryRoot));
                }

                if (result.Success)
                {
                    fileGone = true;
                    logger.LogInformation("DELETED: {FileName}", fileName);
                }
                else
                {
                    failedItems.Add((item.SourceItemId, $"Deletion failed: {result.ErrorMessage}"));
                    logger.LogError("Failed to delete {FileName}: {Error}", fileName, result.ErrorMessage);
                }
            }
            else
            {
                // File doesn't exist on disk, DB row should still be cleaned up.
                fileGone = true;
            }

            if (fileGone)
            {
                try
                {
                    manager.Delete(item.SourceItemId);
                    deleted++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // File is gone but DB delete failed. Log loudly so the
                    // user can see it. The next sync run will pick up the
                    // stale row and re-attempt cleanup. We don't add to
                    // failedItems because the file IS deleted, re-marking
                    // Errored would be wrong.
                    logger.LogError(ex, "DB cleanup failed for {FileName} ({SourceItemId}), file already deleted. Will retry on next sync", fileName, item.SourceItemId);
                }
            }
        }

        if (failedItems.Count > 0)
        {
            try
            {
                // One batch per reason, so each row keeps the reason it failed for. A refusal (outside
                // every library root, a symlinked folder) asks the user for a different fix than a
                // file that could not be removed, and a single generic reason hid which was which.
                foreach (var group in failedItems.GroupBy(f => f.Error, StringComparer.Ordinal))
                {
                    failed += manager.BatchUpdateStatus(
                        group.Select(f => f.SourceItemId),
                        SyncStatus.Errored,
                        group.Key);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to update error status for {Count} items", failedItems.Count);
            }
        }

        logger.LogInformation("Deletion processing complete: {Deleted} deleted, {Failed} failed", deleted, failed);
        return (deleted, failed);
    }
}
