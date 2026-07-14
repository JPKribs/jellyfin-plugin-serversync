using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// State transitions for content sync items. Change detection is size-only;
/// the ETag-based detection it replaced was unstable because Jellyfin's ETag
/// changes whenever UserData changes. <see cref="ProcessMissingItem"/> owns
/// its own writes (delete vs upsert); other Process* methods are pure and
/// leave persistence to the caller.
/// </summary>
public static class SyncStateService
{
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
            UpdateItemMetadata(existingItem, sourcePath, sourceSize, sourceCreateDate, localPath);
            logger.LogDebug("Restored {FileName} (reappeared on source before deletion ran)", Path.GetFileName(sourcePath));
            return existingItem;
        }

        if (existingItem.Status == SyncStatus.Pending && existingItem.PendingType == PendingType.Deletion)
        {
            existingItem.Status = SyncStatus.Queued;
            existingItem.PendingType = null;
            existingItem.StatusDate = DateTime.UtcNow;
            UpdateItemMetadata(existingItem, sourcePath, sourceSize, sourceCreateDate, localPath);
            logger.LogDebug("Restored {FileName} (reappeared on source)", Path.GetFileName(sourcePath));
            return existingItem;
        }

        if (existingItem.Status == SyncStatus.Pending)
        {
            if (HasMetadataChanged(existingItem, sourcePath, sourceSize))
            {
                UpdateItemMetadata(existingItem, sourcePath, sourceSize, sourceCreateDate, localPath);
            }

            return existingItem;
        }

        var sourceChanged = HasMetadataChanged(existingItem, sourcePath, sourceSize);

        if (existingItem.Status == SyncStatus.Queued || existingItem.Status == SyncStatus.Errored)
        {
            try
            {
                if (File.Exists(localPath))
                {
                    var localInfo = new FileInfo(localPath);
                    if (FileValidationService.IsSizeWithinDriftTolerance(localInfo.Length, sourceSize, sizeMatchToleranceBytes))
                    {
                        existingItem.Status = SyncStatus.Synced;
                        existingItem.PendingType = null;
                        existingItem.StatusDate = DateTime.UtcNow;
                        UpdateItemMetadata(existingItem, sourcePath, sourceSize, sourceCreateDate, localPath);
                        logger.LogDebug("Marked {FileName} as synced (local file found with matching size)", Path.GetFileName(localPath));
                        return existingItem;
                    }
                }
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Failed to check local file status for {LocalPath}", localPath);
            }

            if (sourceChanged)
            {
                UpdateItemMetadata(existingItem, sourcePath, sourceSize, sourceCreateDate, localPath);
            }

            return existingItem;
        }

        if (sourceChanged)
        {
            return HandleSourceChanged(existingItem, sourcePath, sourceSize, sourceCreateDate, localPath, replaceMode, logger);
        }

        if (existingItem.Status == SyncStatus.Synced && detectUpdatedFiles)
        {
            return VerifyLocalFileIntegrity(existingItem, localPath, sourceSize, replaceMode, sizeMatchToleranceBytes, logger);
        }

        return existingItem;
    }

    /// <summary>
    /// Processes an item that is missing from the source server. Owns its
    /// own writes because the action varies — delete the row outright, or
    /// upsert with a new soft-delete status — depending on prior state and
    /// the configured deletion mode.
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

        if (item.Status == SyncStatus.Ignored ||
            (item.Status == SyncStatus.Pending && item.PendingType == PendingType.Deletion))
        {
            return new TransitionResult(false, "Already pending deletion or ignored");
        }

        if (item.Status != SyncStatus.Synced)
        {
            manager.DeleteByKey(item.SourceItemId);
            logger.LogDebug("Removed tracking for {FileName} (no longer on source)", Path.GetFileName(item.SourcePath));
            return new TransitionResult(true, "Removed from tracking (not synced)");
        }

        if (!string.IsNullOrEmpty(item.LocalPath) && !File.Exists(item.LocalPath))
        {
            manager.DeleteByKey(item.SourceItemId);
            logger.LogDebug("Removed tracking for {FileName} (gone from both source and local)", Path.GetFileName(item.LocalPath));
            return new TransitionResult(true, "Removed from tracking (no longer on source or local)");
        }

        if (deleteMode == ApprovalMode.RequireApproval)
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
        ILogger logger)
    {
        UpdateItemMetadata(existingItem, sourcePath, sourceSize, sourceCreateDate, localPath);

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
        string localPath)
    {
        item.SourcePath = sourcePath;
        item.SourceSize = sourceSize;
        item.SourceCreateDate = sourceCreateDate;
        item.LocalPath = localPath;
    }
}
