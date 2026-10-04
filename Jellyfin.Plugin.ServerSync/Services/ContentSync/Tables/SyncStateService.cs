using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Utilities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// State transitions for content sync items. Change detection is size-only.
/// the ETag-based detection it replaced was unstable because Jellyfin's ETag
/// changes whenever UserData changes. <see cref="ProcessMissingItem"/> owns
/// its own writes (delete vs upsert). Other Process* methods are pure and
/// leave persistence to the caller.
/// </summary>
public static class SyncStateService
{
    // Starts every reason written when a rename on the source could not be followed on disk, so a
    // later successful move can clear its own reason without touching any other.
    private const string RenameReasonPrefix = "Renamed on the source";

    /// <summary>
    /// Builds a record for a new item discovered on the source server.
    /// Returns null when downloads are disabled. The caller persists.
    /// </summary>
    public static SyncItem? ProcessNewItem(
        LibraryMapping mapping,
        string sourceItemId,
        string sourcePath,
        long sourceSize,
        DateTime sourceCreateDate,
        string localPath,
        ApprovalMode downloadMode,
        long sizeMatchToleranceBytes)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        if (downloadMode == ApprovalMode.Disabled)
        {
            return null;
        }

        var requiresApproval = downloadMode == ApprovalMode.RequireApproval;
        var syncItem = new SyncItem
        {
            SourceLibraryId = mapping.SourceLibraryId,
            LocalLibraryId = mapping.LocalLibraryId,
            SourceItemId = sourceItemId,
            SourcePath = sourcePath,
            SourceSize = sourceSize,
            SourceCreateDate = sourceCreateDate,
            LocalPath = localPath,
            StatusDate = DateTime.UtcNow,
            Status = requiresApproval ? SyncStatus.Pending : SyncStatus.Queued,
            PendingType = requiresApproval ? PendingType.Download : null
        };

        if (File.Exists(localPath))
        {
            var localInfo = new FileInfo(localPath);
            if (FileValidationService.IsSizeWithinDriftTolerance(localInfo.Length, sourceSize, sizeMatchToleranceBytes))
            {
                syncItem.Status = SyncStatus.Synced;
                syncItem.PendingType = null;
            }
        }

        return syncItem;
    }

    /// <summary>
    /// Mutates an existing record based on current source-side state and
    /// returns it. The caller persists.
    /// </summary>
    public static SyncItem ProcessExistingItem(
        SyncItem existingItem,
        string sourcePath,
        long sourceSize,
        DateTime sourceCreateDate,
        string localPath,
        ApprovalMode replaceMode,
        bool detectUpdatedFiles,
        long sizeMatchToleranceBytes,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(existingItem);
        ArgumentNullException.ThrowIfNull(logger);

        if (existingItem.Status == SyncStatus.Ignored)
        {
            return existingItem;
        }

        if (existingItem.Status == SyncStatus.Deleting)
        {
            // Scheduled for deletion but back on the source: restore instead
            // of deleting a file that would immediately re-download.
            existingItem.Status = SyncStatus.Queued;
            existingItem.PendingType = null;
            existingItem.StatusDate = DateTime.UtcNow;
            UpdateItemMetadata(existingItem, sourcePath, sourceSize, sourceCreateDate, localPath, logger);
            logger.LogDebug("Restored {FileName} (reappeared on source before deletion ran)", Path.GetFileName(sourcePath));
            return existingItem;
        }

        if (existingItem.Status == SyncStatus.Pending && existingItem.PendingType == PendingType.Deletion)
        {
            existingItem.Status = SyncStatus.Queued;
            existingItem.PendingType = null;
            existingItem.StatusDate = DateTime.UtcNow;
            UpdateItemMetadata(existingItem, sourcePath, sourceSize, sourceCreateDate, localPath, logger);
            logger.LogDebug("Restored {FileName} (reappeared on source)", Path.GetFileName(sourcePath));
            return existingItem;
        }

        if (existingItem.Status == SyncStatus.Pending)
        {
            if (HasMetadataChanged(existingItem, sourcePath, sourceSize))
            {
                UpdateItemMetadata(existingItem, sourcePath, sourceSize, sourceCreateDate, localPath, logger);
            }

            return existingItem;
        }

        var sourceChanged = HasMetadataChanged(existingItem, sourcePath, sourceSize);

        if (existingItem.Status == SyncStatus.Queued || existingItem.Status == SyncStatus.Errored)
        {
            // Apply a source change before looking on disk, so a rename has moved the local file to
            // its new path (or left the row on the old one) by the time the file is looked for.
            if (sourceChanged)
            {
                UpdateItemMetadata(existingItem, sourcePath, sourceSize, sourceCreateDate, localPath, logger);
            }

            var pathToCheck = sourceChanged ? existingItem.LocalPath ?? localPath : localPath;
            try
            {
                if (File.Exists(pathToCheck))
                {
                    var localInfo = new FileInfo(pathToCheck);
                    if (FileValidationService.IsSizeWithinDriftTolerance(localInfo.Length, sourceSize, sizeMatchToleranceBytes))
                    {
                        existingItem.Status = SyncStatus.Synced;
                        existingItem.PendingType = null;
                        existingItem.StatusDate = DateTime.UtcNow;
                        if (!sourceChanged)
                        {
                            UpdateItemMetadata(existingItem, sourcePath, sourceSize, sourceCreateDate, localPath, logger);
                        }

                        logger.LogDebug("Marked {FileName} as synced (local file found with matching size)", Path.GetFileName(pathToCheck));
                        return existingItem;
                    }
                }
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Failed to check local file status for {LocalPath}", pathToCheck);
            }

            return existingItem;
        }

        if (sourceChanged)
        {
            return HandleSourceChanged(existingItem, sourcePath, sourceSize, sourceCreateDate, localPath, replaceMode, sizeMatchToleranceBytes, logger);
        }

        if (existingItem.Status == SyncStatus.Synced && detectUpdatedFiles)
        {
            // Check the file the row tracks. After a rename that could not be followed on disk the row
            // still points at the old path, and checking the new one would find nothing there and
            // queue a second copy of a file that is already present.
            return VerifyLocalFileIntegrity(existingItem, existingItem.LocalPath ?? localPath, sourceSize, replaceMode, sizeMatchToleranceBytes, logger);
        }

        return existingItem;
    }

    /// <summary>
    /// Processes an item that is missing from the source server. Owns its
    /// own writes because the action varies, delete the row outright, or
    /// upsert with a new soft-delete status, depending on prior state and
    /// the configured deletion mode.
    /// Whether the row goes is decided by the local file, not the row's status. A row whose file is
    /// gone is dropped. A row whose file is still on disk stays so the deletion policy decides what
    /// happens to the file, since dropping it would leave a file nothing tracks any more.
    /// </summary>
    public static TransitionResult ProcessMissingItem(
        ContentSyncTableManager manager,
        SyncItem item,
        ApprovalMode deleteMode,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(logger);

        // Deleting belongs here alongside pending-deletion: the row is already
        // scheduled and the file is still on disk waiting for the next Sync run
        // to remove it. Falling through to the status checks below deleted
        // the tracking row instead, stranding the file permanently with nothing
        // left pointing at it. Reachable whenever a Refresh lands between the
        // mark and the Sync that would execute it, an aborted pre-flight
        // (disk space, circuit breaker, connection), a cancelled run, or just
        // the default 10h refresh / 12h sync cadence drifting.
        if (item.Status == SyncStatus.Ignored ||
            item.Status == SyncStatus.Deleting ||
            (item.Status == SyncStatus.Pending && item.PendingType == PendingType.Deletion))
        {
            return new TransitionResult(false, "Already pending deletion or ignored");
        }

        var localFileExists = !string.IsNullOrEmpty(item.LocalPath) && File.Exists(item.LocalPath);
        if (!localFileExists)
        {
            manager.DeleteByKey(item.SourceItemId);
            if (item.Status == SyncStatus.Synced)
            {
                logger.LogDebug("Removed tracking for {FileName} (gone from both source and local)", Path.GetFileName(item.LocalPath));
                return new TransitionResult(true, "Removed from tracking (no longer on source or local)");
            }

            logger.LogDebug("Removed tracking for {FileName} (no longer on source)", Path.GetFileName(item.SourcePath));
            return new TransitionResult(true, "Removed from tracking (not synced)");
        }

        // A row that is not Synced but has a file on disk is usually an earlier download whose
        // replacement failed or is still waiting. The plugin cannot be sure it wrote that file, so
        // the deletion waits for the user's approval even when deletion is otherwise automatic.
        if (deleteMode == ApprovalMode.RequireApproval || item.Status != SyncStatus.Synced)
        {
            item.Status = SyncStatus.Pending;
            item.PendingType = PendingType.Deletion;
            item.StatusDate = DateTime.UtcNow;
            manager.Upsert(item);
            logger.LogDebug("Marked {FileName} for pending deletion (requires approval)", Path.GetFileName(item.LocalPath));
            return new TransitionResult(true, "Pending deletion approval");
        }

        item.Status = SyncStatus.Deleting;
        item.PendingType = null;
        item.StatusDate = DateTime.UtcNow;
        manager.Upsert(item);
        logger.LogDebug("Marked {FileName} for deletion (missing from source)", Path.GetFileName(item.LocalPath));
        return new TransitionResult(true, "Marked for deletion");
    }

    private static SyncItem HandleSourceChanged(
        SyncItem existingItem,
        string sourcePath,
        long sourceSize,
        DateTime sourceCreateDate,
        string localPath,
        ApprovalMode replaceMode,
        long sizeMatchToleranceBytes,
        ILogger logger)
    {
        var sizeChanged = existingItem.SourceSize != sourceSize;
        UpdateItemMetadata(existingItem, sourcePath, sourceSize, sourceCreateDate, localPath, logger);

        // A rename alone changes no bytes. When the file the row tracks is present at the expected
        // size, whether it moved along or stayed on the old path, there is nothing to download.
        if (!sizeChanged && LocalFileMatches(existingItem.LocalPath, sourceSize, sizeMatchToleranceBytes, logger))
        {
            return existingItem;
        }

        if (replaceMode == ApprovalMode.Disabled)
        {
            return existingItem;
        }

        if (replaceMode == ApprovalMode.RequireApproval)
        {
            existingItem.Status = SyncStatus.Pending;
            existingItem.PendingType = PendingType.Replacement;
            existingItem.StatusDate = DateTime.UtcNow;
            logger.LogDebug("Marked {FileName} for pending replacement (requires approval)", Path.GetFileName(sourcePath));
            return existingItem;
        }

        existingItem.Status = SyncStatus.Queued;
        existingItem.PendingType = null;
        existingItem.StatusDate = DateTime.UtcNow;
        logger.LogDebug("Re-queued {FileName} (source size changed)", Path.GetFileName(sourcePath));
        return existingItem;
    }

    private static SyncItem VerifyLocalFileIntegrity(
        SyncItem existingItem,
        string localPath,
        long sourceSize,
        ApprovalMode replaceMode,
        long sizeMatchToleranceBytes,
        ILogger logger)
    {
        try
        {
            if (File.Exists(localPath))
            {
                var localInfo = new FileInfo(localPath);
                if (sourceSize > 0 && !FileValidationService.IsSizeWithinDriftTolerance(localInfo.Length, sourceSize, sizeMatchToleranceBytes))
                {
                    if (replaceMode == ApprovalMode.Disabled)
                    {
                        return existingItem;
                    }

                    if (replaceMode == ApprovalMode.RequireApproval)
                    {
                        existingItem.Status = SyncStatus.Pending;
                        existingItem.PendingType = PendingType.Replacement;
                    }
                    else
                    {
                        existingItem.Status = SyncStatus.Queued;
                        existingItem.PendingType = null;
                    }

                    existingItem.StatusDate = DateTime.UtcNow;
                    logger.LogDebug("Re-queued {FileName} (local size {LocalSize} != source size {SourceSize})",
                        Path.GetFileName(localPath), localInfo.Length, sourceSize);
                    return existingItem;
                }
            }
            else
            {
                existingItem.Status = SyncStatus.Queued;
                existingItem.PendingType = null;
                existingItem.StatusDate = DateTime.UtcNow;
                existingItem.LocalItemId = null;
                logger.LogDebug("Re-queued {FileName} (local file missing)", Path.GetFileName(localPath));
                return existingItem;
            }
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed to check local file status for {LocalPath}", localPath);
        }

        return existingItem;
    }

    private static bool HasMetadataChanged(SyncItem item, string sourcePath, long sourceSize)
    {
        if (item.SourcePath != sourcePath)
        {
            return true;
        }

        return item.SourceSize != sourceSize;
    }

    private static void UpdateItemMetadata(
        SyncItem item,
        string sourcePath,
        long sourceSize,
        DateTime sourceCreateDate,
        string localPath,
        ILogger logger)
    {
        var renamedOnSource = !string.Equals(item.SourcePath, sourcePath, StringComparison.Ordinal);
        item.SourcePath = sourcePath;
        item.SourceSize = sourceSize;
        item.SourceCreateDate = sourceCreateDate;
        if (renamedOnSource)
        {
            FollowRename(item, localPath, logger);
        }
        else
        {
            item.LocalPath = localPath;
        }
    }

    private static bool LocalFileMatches(string? path, long sourceSize, long sizeMatchToleranceBytes, ILogger logger)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            return File.Exists(path)
                && FileValidationService.IsSizeWithinDriftTolerance(new FileInfo(path).Length, sourceSize, sizeMatchToleranceBytes);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed to check local file status for {LocalPath}", path);
            return false;
        }
    }

    /// <summary>
    /// Follows a rename on the source by moving the local file to its new translated path. Simply
    /// rewriting the row's path left the old file on disk with nothing tracking it, and the next
    /// check found no file at the new path and downloaded a second copy. The move only happens into
    /// a free path, and nothing is ever deleted here. When the new path is taken or the move fails,
    /// the row keeps pointing at the old file and the reason says why.
    /// </summary>
    private static void FollowRename(SyncItem item, string newLocalPath, ILogger logger)
    {
        var oldLocalPath = item.LocalPath;
        if (string.IsNullOrEmpty(oldLocalPath) || string.Equals(oldLocalPath, newLocalPath, StringComparison.Ordinal))
        {
            item.LocalPath = newLocalPath;
            return;
        }

        try
        {
            if (!File.Exists(oldLocalPath))
            {
                // Nothing on disk to carry along, so the row just follows the source.
                item.LocalPath = newLocalPath;
                return;
            }

            // A path differing only in case can name the same file on a case insensitive disk, so it
            // is not treated as taken. The move itself still refuses to overwrite a different file.
            var differsOnlyInCase = string.Equals(oldLocalPath, newLocalPath, StringComparison.OrdinalIgnoreCase);
            if (!differsOnlyInCase && (File.Exists(newLocalPath) || Directory.Exists(newLocalPath)))
            {
                item.Reason = $"{RenameReasonPrefix} to {newLocalPath}, but something already exists there. This row keeps tracking {oldLocalPath} until that is resolved.";
                logger.LogWarning("{Old} was renamed on the source to {New}, but that path is taken, so the local file stays where it is", oldLocalPath, newLocalPath);
                return;
            }

            var newDirectory = Path.GetDirectoryName(newLocalPath);
            if (!string.IsNullOrEmpty(newDirectory))
            {
                Directory.CreateDirectory(newDirectory);
            }

            // Companions are found by the main file's name, so list them before it moves.
            var companions = FileOperationUtilities.GetCompanionFiles(oldLocalPath);
            File.Move(oldLocalPath, newLocalPath);
            item.LocalPath = newLocalPath;

            // Jellyfin indexes the moved file as a new item on its next scan, so the old id no longer
            // names it. Clearing it lets the next resolve pass pick up the new one.
            item.LocalItemId = null;
            if (item.Reason != null && item.Reason.StartsWith(RenameReasonPrefix, StringComparison.Ordinal))
            {
                item.Reason = null;
            }

            MoveCompanionsAlong(companions, oldLocalPath, newLocalPath, logger);
            logger.LogInformation("Moved {Old} to {New} to follow a rename on the source", oldLocalPath, newLocalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            item.LocalPath = File.Exists(oldLocalPath) ? oldLocalPath : newLocalPath;
            item.Reason = $"{RenameReasonPrefix} to {newLocalPath}, but the local file could not be moved there: {ex.Message}";
            logger.LogWarning(ex, "Could not move {Old} to {New} to follow a rename on the source", oldLocalPath, newLocalPath);
        }
    }

    /// <summary>
    /// Moves a renamed file's companions (subtitles, nfo, artwork) next to it under the new name.
    /// Each one moves only into a free name, so an existing file is never overwritten, and a
    /// companion that cannot move stays where it was.
    /// </summary>
    private static void MoveCompanionsAlong(List<string> companions, string oldMainPath, string newMainPath, ILogger logger)
    {
        var oldStem = Path.GetFileNameWithoutExtension(oldMainPath);
        var newStem = Path.GetFileNameWithoutExtension(newMainPath);
        var newDirectory = Path.GetDirectoryName(newMainPath) ?? string.Empty;
        foreach (var companion in companions)
        {
            var name = Path.GetFileName(companion);
            if (name.Length < oldStem.Length)
            {
                continue;
            }

            var destination = Path.Combine(newDirectory, newStem + name[oldStem.Length..]);
            try
            {
                if (string.Equals(companion, destination, StringComparison.Ordinal) || File.Exists(destination))
                {
                    continue;
                }

                File.Move(companion, destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not move companion {Companion} to {Destination}", companion, destination);
            }
        }
    }
}
