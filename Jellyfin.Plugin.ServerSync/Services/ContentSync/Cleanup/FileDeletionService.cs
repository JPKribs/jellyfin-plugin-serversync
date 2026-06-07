using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        // Remove empty parent folders if enabled, bounded by library root
        if (removeEmptyFolders && mainDeleted && !string.IsNullOrEmpty(parentDirectory))
        {
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
        try
        {
            // Normalize the boundary path for comparison
            var normalizedRoot = !string.IsNullOrEmpty(libraryRootPath)
                ? Path.GetFullPath(libraryRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : null;

            // Walk up the directory tree and remove empty folders
            var currentDir = directoryPath;

            while (!string.IsNullOrEmpty(currentDir) && Directory.Exists(currentDir))
            {
                // Stop if we've reached or gone above the library root
                if (normalizedRoot != null)
                {
                    var normalizedCurrent = Path.GetFullPath(currentDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(normalizedCurrent, normalizedRoot, StringComparison.OrdinalIgnoreCase)
                        || !normalizedCurrent.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
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
            if (useRecyclingBin && !string.IsNullOrEmpty(filePath))
            {
                // Move to recycling bin first, then remove from Jellyfin without deleting file
                var recycleResult = DeleteWithRecyclingBin(filePath, config.RecyclingBinPath!, logger);
                if (!recycleResult.Success)
                {
                    return recycleResult;
                }

                // Remove from Jellyfin database only (file already moved)
                return DeleteViaJellyfinApi(localItem, libraryManager, deleteFileLocation: false, logger);
            }
            else
            {
                // Use Jellyfin's library manager to delete the item (handles companion files)
                return DeleteViaJellyfinApi(localItem, libraryManager, deleteFileLocation: true, logger);
            }
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
        var itemsToDelete = manager.GetByStatus(SyncStatus.Deleting).ToList();

        if (itemsToDelete.Count == 0)
        {
            return (0, 0);
        }

        logger.LogInformation("Processing {Count} items marked for deletion", itemsToDelete.Count);

        // Build lookup for library root paths to bound empty folder cleanup
        var libraryRootLookup = config.LibraryMappings
            .Where(m => !string.IsNullOrEmpty(m.SourceLibraryId) && !string.IsNullOrEmpty(m.LocalRootPath))
            .GroupBy(m => m.SourceLibraryId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().LocalRootPath, StringComparer.OrdinalIgnoreCase);

        var deleted = 0;
        var failed = 0;
        var failedItems = new List<(string SourceItemId, string Error)>();

        // Per-item atomicity: file delete → immediate DB row delete. The
        // previous design ran all file deletes first, then a single
        // BatchDelete at the end — if BatchDelete threw (SQLite locked,
        // disk full), the files were gone but the DB still referenced them.
        // A subsequent refresh would see "file missing" + Deleting status
        // and confuse the user with a fake error. Per-item ordering means
        // a transient DB failure leaves the row in place; the next refresh
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
                // File doesn't exist on disk — DB row should still be cleaned up.
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
                    // user can see it; the next sync run will pick up the
                    // stale row and re-attempt cleanup. We don't add to
                    // failedItems because the file IS deleted — re-marking
                    // Errored would be wrong.
                    logger.LogError(ex, "DB cleanup failed for {FileName} ({SourceItemId}) — file already deleted; will retry on next sync", fileName, item.SourceItemId);
                }
            }
        }

        if (failedItems.Count > 0)
        {
            try
            {
                var errorMessage = "Deletion failed";
                failed = manager.BatchUpdateStatus(
                    failedItems.Select(f => f.SourceItemId),
                    SyncStatus.Errored,
                    errorMessage);
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
