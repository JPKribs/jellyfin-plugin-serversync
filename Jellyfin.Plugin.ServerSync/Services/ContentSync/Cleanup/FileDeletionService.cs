using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Downloads;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.ContentSync.Cleanup;

/// <summary>
/// Deletes synced files and the companion files that belong to them.
/// </summary>
public static class FileDeletionService
{
    private static readonly string[] TitleImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".tbn" };

    /// <summary>
    /// Matches artwork Jellyfin and other tools save for a title folder as a whole.
    /// </summary>
    private static readonly Regex TitleImageName = new(
        "^(poster|folder|fanart|backdrop|banner|logo|clearart|thumb|landscape|disc)[0-9]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Extensions of text companions a deleted item may leave behind, such as "name.en.srt".
    /// </summary>
    private static readonly string[] ItemTextExtensions = { ".srt", ".sub", ".idx", ".ass", ".ssa", ".vtt", ".smi", ".txt", ".nfo" };

    /// <summary>
    /// Deletes a file and its companion files permanently.
    /// </summary>
    /// <param name="filePath">The main file to delete.</param>
    /// <param name="logger">Records each file deleted and each failure.</param>
    /// <param name="removeEmptyFolders">Whether to remove parent folders the deletion leaves empty.</param>
    /// <param name="libraryRootPath">The root folder bounding the empty folder cleanup, or null to skip it.</param>
    /// <returns><c>true</c> when the main file was deleted.</returns>
    public static bool DeleteWithCompanions(string filePath, ILogger logger, bool removeEmptyFolders = false, string? libraryRootPath = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

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
                logger.LogDebug("Deleted file: {FileName}", FormatUtilities.SanitizeForLog(Path.GetFileName(filePath)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logger.LogError(ex, "Failed to delete file: {FilePath}", FormatUtilities.SanitizeForLog(filePath));
            return false;
        }

        DeleteCompanionFiles(filePath, logger);
        DeleteItemArtifacts(filePath, logger);

        // A folder left with only title artwork and nfo files counts as empty.
        if (removeEmptyFolders && mainDeleted && !string.IsNullOrEmpty(parentDirectory))
        {
            DeleteTitleArtifacts(parentDirectory, Path.GetFileNameWithoutExtension(filePath), logger, libraryRootPath);
            TryRemoveEmptyFolders(parentDirectory, logger, libraryRootPath);
        }

        return mainDeleted;
    }

    /// <summary>
    /// Deletes a link with everything beside it under the same name, then every folder that leaves
    /// holding only title artwork and nfo files, up to the root folder. A link never goes to the
    /// Recycling Bin and its folders always go, whatever the settings for copies say.
    /// </summary>
    /// <param name="linkPath">The link's .strm file.</param>
    /// <param name="logger">Records each file and folder deleted.</param>
    /// <param name="libraryRootPath">The root folder, which bounds the folders removed.</param>
    /// <returns><c>true</c> when the .strm was deleted.</returns>
    public static bool DeleteLinkWithFolders(string linkPath, ILogger logger, string? libraryRootPath)
    {
        if (!DeleteWithCompanions(linkPath, logger, removeEmptyFolders: true, libraryRootPath))
        {
            return false;
        }

        // A folder is cleared only once the folder below it has gone.
        var stem = Path.GetFileNameWithoutExtension(linkPath);
        var folder = Path.GetDirectoryName(linkPath);
        while (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
        {
            folder = Path.GetDirectoryName(folder);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                continue;
            }

            DeleteTitleArtifacts(folder, stem, logger, libraryRootPath);
            TryRemoveEmptyFolders(folder, logger, libraryRootPath);
        }

        return true;
    }

    /// <summary>
    /// Removes a folder and each parent above it while they are completely empty, stopping at the root folder.
    /// </summary>
    /// <param name="directoryPath">The folder the walk starts at.</param>
    /// <param name="logger">Records each folder removed and each failure.</param>
    /// <param name="libraryRootPath">The root folder the walk never reaches or leaves, or null to skip the cleanup.</param>
    public static void TryRemoveEmptyFolders(string directoryPath, ILogger logger, string? libraryRootPath = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        // Without a root the walk could climb into mount point parents.
        if (string.IsNullOrEmpty(libraryRootPath))
        {
            logger.LogDebug("Skipping empty-folder cleanup for {Directory}: no library root resolved to bound the walk", FormatUtilities.SanitizeForLog(directoryPath));
            return;
        }

        try
        {
            var currentDir = directoryPath;

            while (!string.IsNullOrEmpty(currentDir) && Directory.Exists(currentDir))
            {
                if (!PathContainment.IsStrictlyUnder(currentDir, libraryRootPath))
                {
                    break;
                }

                if (Directory.EnumerateFileSystemEntries(currentDir).Any())
                {
                    break;
                }

                try
                {
                    Directory.Delete(currentDir, recursive: false);
                    logger.LogDebug("Removed empty folder: {FolderPath}", FormatUtilities.SanitizeForLog(currentDir));
                    currentDir = Path.GetDirectoryName(currentDir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(ex, "Could not remove folder (may be in use): {FolderPath}", FormatUtilities.SanitizeForLog(currentDir));
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logger.LogDebug(ex, "Error checking empty folders for {DirectoryPath}", FormatUtilities.SanitizeForLog(directoryPath));
        }
    }

    /// <summary>
    /// Deletes the companion files, such as subtitles, metadata, and images, named after the main file.
    /// </summary>
    /// <param name="mainFilePath">The main file whose name the companions share.</param>
    /// <param name="logger">Records each companion deleted and each failure.</param>
    public static void DeleteCompanionFiles(string mainFilePath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var companions = FileOperationUtilities.GetCompanionFiles(mainFilePath);
        foreach (var companionFile in companions)
        {
            try
            {
                File.Delete(companionFile);
                logger.LogDebug("Deleted companion file: {CompanionFile}", FormatUtilities.SanitizeForLog(Path.GetFileName(companionFile)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Failed to delete companion file: {CompanionFile}", FormatUtilities.SanitizeForLog(Path.GetFileName(companionFile)));
            }
        }
    }

    /// <summary>
    /// Deletes the local images and trickplay folder Jellyfin generates for one media file and names after it.
    /// Another version in the same folder keeps its own.
    /// </summary>
    /// <param name="mainFilePath">The media file the images and trickplay folder are named after.</param>
    /// <param name="logger">Records each deletion and each failure.</param>
    public static void DeleteItemArtifacts(string mainFilePath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

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
            logger.LogDebug("Deleted trickplay folder: {Folder}", FormatUtilities.SanitizeForLog(Path.GetFileName(trickplay)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to delete trickplay folder: {Folder}", FormatUtilities.SanitizeForLog(trickplay));
        }
    }

    /// <summary>
    /// Deletes the title artwork and nfo files left in a folder strictly inside the root folder, once the deleted item was the last thing there.
    /// Any other file or subfolder means the folder still holds something, and then nothing is deleted.
    /// </summary>
    /// <param name="folder">The folder the deleted file was in.</param>
    /// <param name="deletedStem">The deleted file's name without its extension, which marks its own leftovers.</param>
    /// <param name="logger">Records each file deleted and each failure.</param>
    /// <param name="libraryRootPath">The root folder bounding the cleanup, or null to skip it.</param>
    public static void DeleteTitleArtifacts(string folder, string deletedStem, ILogger logger, string? libraryRootPath)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrEmpty(libraryRootPath) || string.IsNullOrEmpty(deletedStem) || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            if (!PathContainment.IsStrictlyUnder(folder, libraryRootPath))
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
            logger.LogDebug(ex, "Could not clean the title files in {Folder}", FormatUtilities.SanitizeForLog(folder));
        }
    }

    /// <summary>
    /// Whether a file is title artwork, an nfo, or a text companion of the deleted item.
    /// </summary>
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
            logger.LogDebug("Deleted file: {FileName}", FormatUtilities.SanitizeForLog(Path.GetFileName(file)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to delete file: {FileName}", FormatUtilities.SanitizeForLog(Path.GetFileName(file)));
        }
    }

    /// <summary>
    /// Moves a file and its companions to the Recycling Bin instead of deleting them.
    /// </summary>
    /// <param name="filePath">The file to move.</param>
    /// <param name="recyclingBinPath">The Recycling Bin folder the file moves into.</param>
    /// <param name="logger">Records the move and any failure.</param>
    /// <returns>Whether the move succeeded, with the reason when it did not.</returns>
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
    /// Deletes an item through Jellyfin's library manager, which also clears it from the library database.
    /// </summary>
    /// <param name="localItem">The Jellyfin item to delete.</param>
    /// <param name="libraryManager">Deletes the item from Jellyfin's library.</param>
    /// <param name="deleteFileLocation">Whether Jellyfin also deletes the item's file from disk.</param>
    /// <param name="logger">Records a failed deletion.</param>
    /// <returns>Whether the deletion succeeded, with the reason when it did not.</returns>
    public static DeletionResult DeleteViaJellyfinApi(
        BaseItem localItem,
        ILibraryManager libraryManager,
        bool deleteFileLocation,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(localItem);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            libraryManager.DeleteItem(
                localItem,
                new DeleteOptions { DeleteFileLocation = deleteFileLocation },
                localItem.GetParent(),
                notifyParentItem: true);

            return new DeletionResult(true);
        }

        // Jellyfin can throw anything here, and the caller needs a result, not an exception.
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete item via Jellyfin API: {ItemName}", FormatUtilities.SanitizeForLog(localItem.Name));
            return new DeletionResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Deletes a local file to the Recycling Bin or permanently, as the settings say, then removes its item from Jellyfin without touching the disk.
    /// Jellyfin's own file deletion is never used, since it removes a movie's whole folder with every other file in it.
    /// </summary>
    /// <param name="filePath">The file to delete, or null to use the item's own path.</param>
    /// <param name="localItem">The Jellyfin item for the file, or null when the file is not in the library.</param>
    /// <param name="libraryManager">Removes the item from Jellyfin's library.</param>
    /// <param name="config">Supplies the Recycling Bin, empty folder, and root folder settings.</param>
    /// <param name="logger">Records each deletion and each failure.</param>
    /// <returns>Whether the deletion succeeded, with the reason when it did not.</returns>
    public static DeletionResult DeleteLocalFile(
        string? filePath,
        BaseItem? localItem,
        ILibraryManager libraryManager,
        PluginConfiguration config,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        var useRecyclingBin = config.EnableRecyclingBin && !string.IsNullOrEmpty(config.RecyclingBinPath);

        if (localItem != null)
        {
            var path = !string.IsNullOrEmpty(filePath) ? filePath : localItem.Path;

            // A file already gone leaves only a stale Jellyfin entry to remove.
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

            return DeleteViaJellyfinApi(localItem, libraryManager, deleteFileLocation: false, logger);
        }

        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            if (useRecyclingBin)
            {
                return DeleteWithRecyclingBin(filePath, config.RecyclingBinPath!, logger);
            }

            return DeleteWithCompanions(filePath, logger)
                ? new DeletionResult(true)
                : new DeletionResult(false, "Failed to delete file");
        }

        return new DeletionResult(true);
    }

    /// <summary>
    /// Whether an item is one plain file, meaning not a disc structure and not split into stacked parts.
    /// Anything that is not a video is one file.
    /// </summary>
    /// <param name="localItem">The Jellyfin item to classify.</param>
    /// <returns><c>true</c> when the item is a single file.</returns>
    public static bool IsSingleFileItem(BaseItem localItem)
        => localItem is not Video video
            || (!video.IsStacked && video.VideoType is MediaBrowser.Model.Entities.VideoType.VideoFile or MediaBrowser.Model.Entities.VideoType.Iso);

    /// <summary>
    /// Says why a library item cannot be deleted as a single file, or null when it can.
    /// A disc structure, a stacked item, a file not on disk, and a path outside every root folder or through a symlinked folder are each refused with nothing touched.
    /// </summary>
    /// <param name="path">The path that would be deleted, which may be the item's own path.</param>
    /// <param name="localItem">The Jellyfin item, checked for disc structures and stacked parts.</param>
    /// <param name="config">Supplies the root folders the path must lie inside.</param>
    /// <returns>The reason for the refusal, or null when the deletion may go ahead.</returns>
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
    /// Deletes the file and the row of every item the Content sync table marks as Deleting.
    /// </summary>
    /// <param name="manager">The Content sync table the Deleting rows are read from and removed from.</param>
    /// <param name="config">Supplies the Removed files, Recycling Bin, and root folder settings.</param>
    /// <param name="logger">Records each deletion, refusal, and failure.</param>
    /// <param name="cancellationToken">Stops the pass before the next item.</param>
    /// <returns>How many items were deleted and how many failed.</returns>
    public static (int Deleted, int Failed) ProcessPendingDeletions(
        ContentSyncTableManager manager,
        PluginConfiguration config,
        ILogger logger,
        CancellationToken cancellationToken)
        => ProcessPendingDeletions(manager, config, logger, null, cancellationToken);

    /// <summary>
    /// Deletes the file and then the row of every item marked as Deleting, one item at a time, so a failed row delete never leaves other rows pointing at deleted files.
    /// A link is deleted whatever Removed files says, and a copy only while Removed files deletes.
    /// </summary>
    /// <param name="manager">The Content sync table the Deleting rows are read from and removed from.</param>
    /// <param name="config">Supplies the Removed files, Recycling Bin, and root folder settings.</param>
    /// <param name="logger">Records each deletion, refusal, and failure.</param>
    /// <param name="removedPaths">Receives each deleted file's path so the caller can refresh only the folders that changed, or null.</param>
    /// <param name="cancellationToken">Stops the pass before the next item.</param>
    /// <returns>How many items were deleted and how many failed.</returns>
    public static (int Deleted, int Failed) ProcessPendingDeletions(
        ContentSyncTableManager manager,
        PluginConfiguration config,
        ILogger logger,
        ICollection<string>? removedPaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        var deletesCopies = config.DeletesMissing;
        var itemsToDelete = manager.GetByStatus(SyncStatus.Deleting).Where(item => deletesCopies || item.StorageMode.IsLink()).ToList();

        if (itemsToDelete.Count == 0)
        {
            return (0, 0);
        }

        logger.LogInformation("Processing {Count} items marked for deletion", itemsToDelete.Count);

        var libraryRootLookup = config.GetAllLibraryMappings()
            .Where(m => !string.IsNullOrEmpty(m.SourceLibraryId) && !string.IsNullOrEmpty(m.LocalRootPath))
            .GroupBy(m => m.SourceLibraryId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().LocalRootPath, StringComparer.OrdinalIgnoreCase);

        var deleted = 0;
        var failed = 0;
        var failedItems = new List<(string SourceItemId, string Error)>();

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
                // An unattended run must never delete outside every root folder, whatever the row says.
                var containingRoot = FileValidationService.GetContainingLibraryRoot(localPath, config);
                if (containingRoot == null)
                {
                    failedItems.Add((item.SourceItemId, "Deletion refused: path is outside all configured library roots"));
                    logger.LogError("Refused to delete {FileName}: {Path} is outside all configured library roots", FormatUtilities.SanitizeForLog(fileName), FormatUtilities.SanitizeForLog(localPath));
                    continue;
                }

                if (FileValidationService.HasSymlinkedDirectoryComponent(localPath, containingRoot))
                {
                    failedItems.Add((item.SourceItemId, "Deletion refused: path traverses a symlinked directory"));
                    logger.LogError("Refused to delete {FileName}: {Path} traverses a symlinked directory, so the physical file may live outside the library", FormatUtilities.SanitizeForLog(fileName), FormatUtilities.SanitizeForLog(localPath));
                    continue;
                }

                DeletionResult result;
                if (item.StorageMode.IsLink() && LinkFiles.IsOwnLink(localPath))
                {
                    result = new DeletionResult(DeleteLinkWithFolders(localPath, logger, containingRoot));
                }
                else if (item.StorageMode.IsLink())
                {
                    // A file a user put in the link's place is theirs, so it stays.
                    logger.LogInformation("Left {Path} in place: the link there was replaced by a file Server Sync did not write", FormatUtilities.SanitizeForLog(localPath));
                    result = new DeletionResult(true);
                }
                else if (config.EnableRecyclingBin && !string.IsNullOrEmpty(config.RecyclingBinPath))
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
                    removedPaths?.Add(localPath);
                    logger.LogInformation("DELETED: {FileName}", FormatUtilities.SanitizeForLog(fileName));
                }
                else
                {
                    failedItems.Add((item.SourceItemId, $"Deletion failed: {result.ErrorMessage}"));
                    logger.LogError("Failed to delete {FileName}: {Error}", FormatUtilities.SanitizeForLog(fileName), FormatUtilities.SanitizeForLog(result.ErrorMessage, 300));
                }
            }
            else
            {
                // A row whose file is already gone still has to go.
                fileGone = true;
            }

            if (fileGone)
            {
                try
                {
                    if (item.StorageMode == StorageMode.Copy)
                    {
                        manager.NoteRemovedCopy(item.LocalPath, item.OriginServerId);
                    }

                    manager.Delete(item.SourceItemId);
                    deleted++;
                }

                // The file is gone, so the next scan retries the row.
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "DB cleanup failed for {FileName} ({SourceItemId}), file already deleted. Will retry on next sync", FormatUtilities.SanitizeForLog(fileName), FormatUtilities.SanitizeForLog(item.SourceItemId));
                }
            }
        }

        if (failedItems.Count > 0)
        {
            try
            {
                // Errored rows retry as downloads, so a failed deletion waits as Pending.
                foreach (var (sourceItemId, error) in failedItems)
                {
                    manager.UpdateStatus(sourceItemId, SyncStatus.Pending, PendingType.Deletion, errorMessage: error);
                    failed++;
                }
            }

            // A failed status write must not hide the deletions already made.
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to update error status for {Count} items", failedItems.Count);
            }
        }

        logger.LogInformation("Deletion processing complete: {Deleted} deleted, {Failed} failed", deleted, failed);
        return (deleted, failed);
    }
}
