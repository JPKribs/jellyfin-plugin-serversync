using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Downloads;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;

/// <summary>
/// Decides each Content row's next status from what the source and the disk hold. A change is told by size alone, since Jellyfin's ETag moves with user data.
/// </summary>
public static class SyncStateService
{
    /// <summary>
    /// Starts every reason for a rename that was not followed, so a later move clears only its own.
    /// </summary>
    private const string RenameReasonPrefix = "Renamed on the source";

    /// <summary>
    /// Builds the row for an item first seen on the source, which the caller saves. A file already at the path with the source's size counts as the same file, and any other follows <paramref name="replaceMode"/>.
    /// </summary>
    /// <param name="mapping">The library mapping the item came through.</param>
    /// <param name="sourceItemId">The item's id on the source.</param>
    /// <param name="sourcePath">The item's path on the source.</param>
    /// <param name="sourceSize">The item's size on the source, in bytes.</param>
    /// <param name="sourceCreateDate">When the item was created on the source.</param>
    /// <param name="localPath">Where the file belongs on this server.</param>
    /// <param name="downloadMode">Whether new files download on their own, wait for approval, or are left alone.</param>
    /// <param name="replaceMode">Whether a different file already at the path is replaced on its own, waits, or is left alone.</param>
    /// <param name="sizeMatchToleranceBytes">How far a local size may differ from the source's and still match.</param>
    /// <returns>The new row, or null when the item is not to be tracked.</returns>
    public static SyncItem? ProcessNewItem(
        LibraryMapping mapping,
        string sourceItemId,
        string sourcePath,
        long sourceSize,
        DateTime sourceCreateDate,
        string localPath,
        ApprovalMode downloadMode,
        ApprovalMode replaceMode,
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
            else if (replaceMode == ApprovalMode.Disabled)
            {
                return null;
            }
            else if (replaceMode == ApprovalMode.RequireApproval || requiresApproval)
            {
                syncItem.Status = SyncStatus.Pending;
                syncItem.PendingType = PendingType.Replacement;
            }
        }

        return syncItem;
    }

    /// <summary>
    /// Updates a row from what the source holds for its item, which the caller saves.
    /// </summary>
    /// <param name="existingItem">The row to update.</param>
    /// <param name="sourcePath">The item's path on the source.</param>
    /// <param name="sourceSize">The item's size on the source, in bytes.</param>
    /// <param name="sourceCreateDate">When the item was created on the source.</param>
    /// <param name="localPath">Where the file belongs on this server.</param>
    /// <param name="replaceMode">Whether a changed file is replaced on its own, waits for approval, or is left alone.</param>
    /// <param name="detectUpdatedFiles">Whether a synced file is checked on disk.</param>
    /// <param name="sizeMatchToleranceBytes">How far a local size may differ from the source's and still match.</param>
    /// <param name="logger">Writes what was decided and why.</param>
    /// <returns>The updated row.</returns>
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

        // Back on the source, so deleting the file would only download it again.
        if (existingItem.Status == SyncStatus.Deleting)
        {
            existingItem.Status = SyncStatus.Queued;
            existingItem.PendingType = null;
            existingItem.StatusDate = DateTime.UtcNow;
            UpdateItemMetadata(existingItem, sourcePath, sourceSize, sourceCreateDate, localPath, logger);
            logger.LogDebug("Restored {FileName} (reappeared on source before deletion ran)", FormatUtilities.SanitizeForLog(Path.GetFileName(sourcePath)));
            return existingItem;
        }

        if (existingItem.Status == SyncStatus.Pending && existingItem.PendingType == PendingType.Deletion)
        {
            existingItem.Status = SyncStatus.Queued;
            existingItem.PendingType = null;
            existingItem.StatusDate = DateTime.UtcNow;
            UpdateItemMetadata(existingItem, sourcePath, sourceSize, sourceCreateDate, localPath, logger);
            logger.LogDebug("Restored {FileName} (reappeared on source)", FormatUtilities.SanitizeForLog(Path.GetFileName(sourcePath)));
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
            // A rename must settle the row's path before the file is looked for.
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

                        logger.LogDebug("Marked {FileName} as synced (local file found with matching size)", FormatUtilities.SanitizeForLog(Path.GetFileName(pathToCheck)));
                        return existingItem;
                    }
                }
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Failed to check local file status for {LocalPath}", FormatUtilities.SanitizeForLog(pathToCheck));
            }

            return existingItem;
        }

        if (sourceChanged)
        {
            return HandleSourceChanged(existingItem, sourcePath, sourceSize, sourceCreateDate, localPath, replaceMode, sizeMatchToleranceBytes, logger);
        }

        // After a rename that was not followed, the new path is empty and would queue a second copy.
        if (existingItem.Status == SyncStatus.Synced && detectUpdatedFiles)
        {
            return VerifyLocalFileIntegrity(existingItem, existingItem.LocalPath ?? localPath, sourceSize, replaceMode, sizeMatchToleranceBytes, logger);
        }

        return existingItem;
    }

    /// <summary>
    /// The reason a row carries once its file is hidden.
    /// </summary>
    public const string HiddenReason = "Hidden with a .ignore file because it is gone from the source";

    /// <summary>
    /// Marks a row whose item is gone from the source as Ignored because its folder was hidden. A row whose file is gone is dropped instead.
    /// </summary>
    /// <param name="manager">The content sync table.</param>
    /// <param name="item">The row to mark.</param>
    /// <param name="logger">Writes what was decided and why.</param>
    /// <returns>What changed.</returns>
    public static TransitionResult MarkHidden(ContentSyncTableManager manager, SyncItem item, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(logger);
        if (string.IsNullOrEmpty(item.LocalPath) || !File.Exists(item.LocalPath))
        {
            manager.DeleteByKey(item.SourceItemId);
            return new TransitionResult(true, "Removed from tracking (no longer on source)");
        }

        item.Status = SyncStatus.Ignored;
        item.PendingType = null;
        item.StatusDate = DateTime.UtcNow;
        item.Reason = HiddenReason;
        manager.Upsert(item);
        return new TransitionResult(true, "Hidden with a .ignore file");
    }

    /// <summary>
    /// Settles a row whose item is gone from the source, and saves the result itself. A row whose file is gone is dropped, and a row whose file is still on disk stays so Removed files decides what happens to it.
    /// </summary>
    /// <param name="manager">The content sync table.</param>
    /// <param name="item">The row whose item is gone from the source.</param>
    /// <param name="deleteMode">How the deletion is approved.</param>
    /// <param name="logger">Writes what was decided and why.</param>
    /// <param name="ownLink">Whether the row's file is a link, which is removed without asking.</param>
    /// <returns>What changed.</returns>
    public static TransitionResult ProcessMissingItem(
        ContentSyncTableManager manager,
        SyncItem item,
        ApprovalMode deleteMode,
        ILogger logger,
        bool ownLink = false)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(logger);

        // A scheduled row keeps its tracking, or its file would be stranded.
        if (item.Status == SyncStatus.Ignored ||
            item.Status == SyncStatus.Deleting ||
            (item.Status == SyncStatus.Pending && item.PendingType == PendingType.Deletion))
        {
            return new TransitionResult(false, "Already pending deletion or ignored");
        }

        var localFileExists = !string.IsNullOrEmpty(item.LocalPath) && File.Exists(item.LocalPath);

        // A file here before it was matched is this server's own, so only the tracking goes.
        if (localFileExists && item.Kept == KeptReason.Own && !ownLink)
        {
            manager.DeleteByKey(item.SourceItemId);
            logger.LogDebug("Kept {FileName}: it was on this server before it was matched to a source's file, so the source removing its own leaves it here", FormatUtilities.SanitizeForLog(Path.GetFileName(item.LocalPath)));
            return new TransitionResult(true, "Kept, since the file was this server's own");
        }

        if (!localFileExists)
        {
            manager.DeleteByKey(item.SourceItemId);
            if (item.Status == SyncStatus.Synced)
            {
                logger.LogDebug("Removed tracking for {FileName} (gone from both source and local)", FormatUtilities.SanitizeForLog(Path.GetFileName(item.LocalPath)));
                return new TransitionResult(true, "Removed from tracking (no longer on source or local)");
            }

            logger.LogDebug("Removed tracking for {FileName} (no longer on source)", FormatUtilities.SanitizeForLog(Path.GetFileName(item.SourcePath)));
            return new TransitionResult(true, "Removed from tracking (not synced)");
        }

        var settled = ownLink || item.Status == SyncStatus.Synced || (item.StorageMode == StorageMode.Link && Links.LinkVerifier.Flagged(item));
        // An unsettled row's file may not be this plugin's, so its deletion waits for approval.
        if (deleteMode == ApprovalMode.RequireApproval || !settled)
        {
            item.Status = SyncStatus.Pending;
            item.PendingType = PendingType.Deletion;
            item.StatusDate = DateTime.UtcNow;
            manager.Upsert(item);
            logger.LogDebug("Marked {FileName} for pending deletion (requires approval)", FormatUtilities.SanitizeForLog(Path.GetFileName(item.LocalPath)));
            return new TransitionResult(true, "Pending deletion approval");
        }

        item.Status = SyncStatus.Deleting;
        item.PendingType = null;
        item.StatusDate = DateTime.UtcNow;
        manager.Upsert(item);
        logger.LogDebug("Marked {FileName} for deletion (missing from source)", FormatUtilities.SanitizeForLog(Path.GetFileName(item.LocalPath)));
        return new TransitionResult(true, "Marked for deletion");
    }

    /// <summary>
    /// Applies a changed path or size to a synced row, and queues a replacement when the bytes changed.
    /// </summary>
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

        // A rename alone changes no bytes, so a file at the expected size needs no download.
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
            logger.LogDebug("Marked {FileName} for pending replacement (requires approval)", FormatUtilities.SanitizeForLog(Path.GetFileName(sourcePath)));
            return existingItem;
        }

        existingItem.Status = SyncStatus.Queued;
        existingItem.PendingType = null;
        existingItem.StatusDate = DateTime.UtcNow;
        logger.LogDebug("Re-queued {FileName} (source size changed)", FormatUtilities.SanitizeForLog(Path.GetFileName(sourcePath)));
        return existingItem;
    }

    /// <summary>
    /// Queues a synced row again when its file is missing or its size does not match the source.
    /// </summary>
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
                        FormatUtilities.SanitizeForLog(Path.GetFileName(localPath)), localInfo.Length, sourceSize);
                    return existingItem;
                }
            }
            else
            {
                existingItem.Status = SyncStatus.Queued;
                existingItem.PendingType = null;
                existingItem.StatusDate = DateTime.UtcNow;
                existingItem.LocalItemId = null;
                logger.LogDebug("Re-queued {FileName} (local file missing)", FormatUtilities.SanitizeForLog(Path.GetFileName(localPath)));
                return existingItem;
            }
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed to check local file status for {LocalPath}", FormatUtilities.SanitizeForLog(localPath));
        }

        return existingItem;
    }

    private static bool HasMetadataChanged(SyncItem item, string sourcePath, long sourceSize)
    {
        return item.SourcePath != sourcePath || item.SourceSize != sourceSize;
    }

    /// <summary>
    /// Copies the source's path, size, and creation date onto a row, and follows a rename on the source.
    /// </summary>
    /// <param name="item">The row to update.</param>
    /// <param name="sourcePath">The item's path on the source.</param>
    /// <param name="sourceSize">The item's size on the source, in bytes.</param>
    /// <param name="sourceCreateDate">When the item was created on the source.</param>
    /// <param name="localPath">Where the file belongs on this server.</param>
    /// <param name="logger">Writes what was decided and why.</param>
    internal static void UpdateItemMetadata(
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
            logger.LogWarning(ex, "Failed to check local file status for {LocalPath}", FormatUtilities.SanitizeForLog(path));
            return false;
        }
    }

    /// <summary>
    /// Follows a rename on the source by moving the local file into a free path, so the old file is not left untracked and downloaded twice. When the path is taken or the move fails, the row keeps the old file and its reason says why.
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
                item.LocalPath = newLocalPath;
                return;
            }

            var differsOnlyInCase = string.Equals(oldLocalPath, newLocalPath, StringComparison.OrdinalIgnoreCase);

            // A path differing only in case can name the same file, so it is not taken.
            if (!differsOnlyInCase && (File.Exists(newLocalPath) || Directory.Exists(newLocalPath)))
            {
                item.Reason = $"{RenameReasonPrefix} to {newLocalPath}, but something already exists there. This row keeps tracking {oldLocalPath} until that is resolved.";
                logger.LogWarning("{Old} was renamed on the source to {New}, but that path is taken, so the local file stays where it is", FormatUtilities.SanitizeForLog(oldLocalPath), FormatUtilities.SanitizeForLog(newLocalPath));
                return;
            }

            var newDirectory = Path.GetDirectoryName(newLocalPath);
            if (!string.IsNullOrEmpty(newDirectory))
            {
                Directory.CreateDirectory(newDirectory);
            }

            var companions = FileOperationUtilities.GetCompanionFiles(oldLocalPath);
            File.Move(oldLocalPath, newLocalPath);
            item.LocalPath = newLocalPath;
            item.LocalItemId = null;
            if (item.Reason != null && item.Reason.StartsWith(RenameReasonPrefix, StringComparison.Ordinal))
            {
                item.Reason = null;
            }

            MoveCompanionsAlong(companions, oldLocalPath, newLocalPath, logger);
            logger.LogDebug("Moved {Old} to {New} to follow a rename on the source", FormatUtilities.SanitizeForLog(oldLocalPath), FormatUtilities.SanitizeForLog(newLocalPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            item.LocalPath = File.Exists(oldLocalPath) ? oldLocalPath : newLocalPath;
            item.Reason = $"{RenameReasonPrefix} to {newLocalPath}, but the local file could not be moved there: {ex.Message}";
            logger.LogWarning(ex, "Could not move {Old} to {New} to follow a rename on the source", FormatUtilities.SanitizeForLog(oldLocalPath), FormatUtilities.SanitizeForLog(newLocalPath));
        }
    }

    /// <summary>
    /// Moves a renamed file's companions beside it under the new name. Each moves only into a free name, and one that cannot move stays where it was.
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
                logger.LogWarning(ex, "Could not move companion {Companion} to {Destination}", FormatUtilities.SanitizeForLog(companion), FormatUtilities.SanitizeForLog(destination));
            }
        }
    }
}
