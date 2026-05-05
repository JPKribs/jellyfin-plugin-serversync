using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Service for managing sync item state transitions.
/// </summary>
public static class SyncStateService
{
    /// <summary>
    /// Processes a new item discovered on the source server.
    /// </summary>
    /// <param name="database">Sync database.</param>
    /// <param name="mapping">Library mapping configuration.</param>
    /// <param name="sourceItemId">Source item ID.</param>
    /// <param name="sourcePath">Source file path.</param>
    /// <param name="sourceSize">Source file size.</param>
    /// <param name="sourceCreateDate">Source creation date.</param>
    /// <param name="sourceETag">Source ETag for change detection.</param>
    /// <param name="localPath">Translated local path.</param>
    /// <param name="downloadMode">Approval mode for new downloads.</param>
    /// <param name="sizeMatchToleranceBytes">Tolerance in bytes for size comparison (0 = strict).</param>
    /// <returns>Transition result.</returns>
    public static TransitionResult ProcessNewItem(
        SyncDatabase database,
        LibraryMapping mapping,
        string sourceItemId,
        string sourcePath,
        long sourceSize,
        DateTime sourceCreateDate,
        string? sourceETag,
        string localPath,
        ApprovalMode downloadMode,
        long sizeMatchToleranceBytes)
    {
        // If download new content is disabled, don't track new items at all
        if (downloadMode == ApprovalMode.Disabled)
        {
            return new TransitionResult(false, "Download new content is disabled");
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
            SourceETag = sourceETag,
            LocalPath = localPath,
            StatusDate = DateTime.UtcNow,
            Status = requiresApproval ? SyncStatus.Pending : SyncStatus.Queued,
            PendingType = requiresApproval ? PendingType.Download : null
        };

        // Check if local file already exists with matching size
        if (File.Exists(localPath))
        {
            var localInfo = new FileInfo(localPath);
            if (FileValidationService.IsSizeWithinDriftTolerance(localInfo.Length, sourceSize, sizeMatchToleranceBytes))
            {
                syncItem.Status = SyncStatus.Synced;
                syncItem.PendingType = null;
            }
        }

        database.Upsert(syncItem);

        var statusMsg = syncItem.Status == SyncStatus.Synced
            ? "Already synced (local file exists)"
            : requiresApproval ? "Pending approval" : "Queued for download";

        return new TransitionResult(true, statusMsg);
    }

    /// <summary>
    /// Processes an existing item that was found on the source server.
    /// Handles state transitions based on source changes and configuration.
    /// </summary>
    /// <param name="database">Sync database.</param>
    /// <param name="existingItem">Existing sync item from database.</param>
    /// <param name="sourcePath">Current source file path.</param>
    /// <param name="sourceSize">Current source file size.</param>
    /// <param name="sourceCreateDate">Current source creation date.</param>
    /// <param name="sourceETag">Current source ETag.</param>
    /// <param name="localPath">Translated local path.</param>
    /// <param name="replaceMode">Approval mode for replacements.</param>
    /// <param name="detectUpdatedFiles">Whether to check local file integrity.</param>
    /// <param name="changeDetectionPolicy">Policy for detecting source changes.</param>
    /// <param name="sizeMatchToleranceBytes">Tolerance in bytes for size comparison (0 = strict).</param>
    /// <param name="logger">Logger for status messages.</param>
    /// <returns>Transition result.</returns>
    public static TransitionResult ProcessExistingItem(
        SyncDatabase database,
        SyncItem existingItem,
        string sourcePath,
        long sourceSize,
        DateTime sourceCreateDate,
        string? sourceETag,
        string localPath,
        ApprovalMode replaceMode,
        bool detectUpdatedFiles,
        ChangeDetectionPolicy changeDetectionPolicy,
        long sizeMatchToleranceBytes,
        ILogger logger)
    {
        // Ignored items stay in their state
        if (existingItem.Status == SyncStatus.Ignored)
        {
            return new TransitionResult(false, "Item is ignored");
        }

        // If item was pending deletion but now exists on source, restore it
        if (existingItem.Status == SyncStatus.Pending && existingItem.PendingType == PendingType.Deletion)
        {
            existingItem.Status = SyncStatus.Queued;
            existingItem.PendingType = null;
            existingItem.StatusDate = DateTime.UtcNow;
            UpdateItemMetadata(existingItem, sourcePath, sourceSize, sourceCreateDate, sourceETag, localPath);
            database.Upsert(existingItem);
            logger.LogInformation("Restored {FileName} (reappeared on source)", Path.GetFileName(sourcePath));
            return new TransitionResult(true, "Restored from pending deletion");
        }

        // Pending items (Download or Replacement) stay pending but update metadata
        if (existingItem.Status == SyncStatus.Pending)
        {
            if (HasMetadataChanged(existingItem, sourcePath, sourceSize, sourceETag, changeDetectionPolicy))
            {
                UpdateItemMetadata(existingItem, sourcePath, sourceSize, sourceCreateDate, sourceETag, localPath);
                database.Upsert(existingItem);
                return new TransitionResult(true, "Metadata updated (still pending)");
            }

            return new TransitionResult(false, "No changes (pending)");
        }

        // Check for source changes using configured detection policy
        var sourceChanged = HasMetadataChanged(existingItem, sourcePath, sourceSize, sourceETag, changeDetectionPolicy);

        // Queued/Errored items: check if local file has appeared since last scan
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
                        UpdateItemMetadata(existingItem, sourcePath, sourceSize, sourceCreateDate, sourceETag, localPath);
                        database.Upsert(existingItem);
                        logger.LogInformation("Marked {FileName} as synced (local file found with matching size)", Path.GetFileName(localPath));
                        return new TransitionResult(true, "Synced (local file found)");
                    }

                    logger.LogDebug(
                        "File exists but size mismatch for {FileName}: local={LocalSize}, source={SourceSize}, path={LocalPath}",
                        Path.GetFileName(localPath),
                        localInfo.Length,
                        sourceSize,
                        localPath);
                }
                else
                {
                    logger.LogDebug(
                        "Local file not found for {Status} item {FileName}: path={LocalPath}",
                        existingItem.Status,
                        Path.GetFileName(localPath),
                        localPath);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to check local file status for {LocalPath}", localPath);
            }

            // Still queued/errored - update metadata if source changed
            if (sourceChanged)
            {
                UpdateItemMetadata(existingItem, sourcePath, sourceSize, sourceCreateDate, sourceETag, localPath);
                database.Upsert(existingItem);
                return new TransitionResult(true, "Metadata updated (still " + existingItem.Status.ToString().ToLowerInvariant() + ")");
            }

            return new TransitionResult(false, "No changes (" + existingItem.Status.ToString().ToLowerInvariant() + ")");
        }

        // Handle source changes for Synced items
        if (sourceChanged)
        {
            return HandleSourceChanged(database, existingItem, sourcePath, sourceSize, sourceCreateDate, sourceETag, localPath, replaceMode, logger);
        }

        // For Synced items with detectUpdatedFiles enabled, verify local file integrity
        if (existingItem.Status == SyncStatus.Synced && detectUpdatedFiles)
        {
            return VerifyLocalFileIntegrity(database, existingItem, localPath, sourceSize, replaceMode, sizeMatchToleranceBytes, logger);
        }

        return new TransitionResult(false, "No changes");
    }

    /// <summary>
    /// Processes an item that is missing from the source server.
    /// </summary>
    /// <param name="database">Sync database.</param>
    /// <param name="item">Sync item that is no longer on source.</param>
    /// <param name="deleteMode">Approval mode for deletions.</param>
    /// <param name="logger">Logger for status messages.</param>
    /// <returns>Transition result.</returns>
    public static TransitionResult ProcessMissingItem(
        SyncDatabase database,
        SyncItem item,
        ApprovalMode deleteMode,
        ILogger logger)
    {
        // Skip items already pending deletion or ignored
        if (item.Status == SyncStatus.Ignored ||
            (item.Status == SyncStatus.Pending && item.PendingType == PendingType.Deletion))
        {
            return new TransitionResult(false, "Already pending deletion or ignored");
        }

        // Only process synced items (they have local files to potentially delete)
        if (item.Status != SyncStatus.Synced)
        {
            // Remove non-synced items from tracking since source no longer has them
            database.Delete(item.SourceItemId);
            logger.LogInformation("Removed tracking for {FileName} (no longer on source)", Path.GetFileName(item.SourcePath));
            return new TransitionResult(true, "Removed from tracking (not synced)");
        }

        // If the local file is also gone, just remove from tracking — nothing left to delete
        if (!string.IsNullOrEmpty(item.LocalPath) && !File.Exists(item.LocalPath))
        {
            database.Delete(item.SourceItemId);
            logger.LogInformation("Removed tracking for {FileName} (gone from both source and local)", Path.GetFileName(item.LocalPath));
            return new TransitionResult(true, "Removed from tracking (no longer on source or local)");
        }

        // Mark for deletion based on approval mode (local file still exists)
        if (deleteMode == ApprovalMode.RequireApproval)
        {
            item.Status = SyncStatus.Pending;
            item.PendingType = PendingType.Deletion;
            item.StatusDate = DateTime.UtcNow;
            database.Upsert(item);
            logger.LogInformation("Marked {FileName} for pending deletion (requires approval)", Path.GetFileName(item.LocalPath));
            return new TransitionResult(true, "Pending deletion approval");
        }
        else
        {
            // Auto-deletion enabled - mark for deletion
            item.Status = SyncStatus.Deleting;
            item.PendingType = null;
            item.StatusDate = DateTime.UtcNow;
            database.Upsert(item);
            logger.LogInformation("Marked {FileName} for deletion (missing from source)", Path.GetFileName(item.LocalPath));
            return new TransitionResult(true, "Marked for deletion");
        }
    }

    /// <summary>
    /// Handles source file changes for synced items.
    /// </summary>
    private static TransitionResult HandleSourceChanged(
        SyncDatabase database,
        SyncItem existingItem,
        string sourcePath,
        long sourceSize,
        DateTime sourceCreateDate,
        string? sourceETag,
        string localPath,
        ApprovalMode replaceMode,
        ILogger logger)
    {
        // If replace is disabled, don't queue the update
        if (replaceMode == ApprovalMode.Disabled)
        {
            UpdateItemMetadata(existingItem, sourcePath, sourceSize, sourceCreateDate, sourceETag, localPath);
            database.Upsert(existingItem);
            return new TransitionResult(true, "Metadata updated (replace disabled)");
        }

        var oldETag = existingItem.SourceETag;
        UpdateItemMetadata(existingItem, sourcePath, sourceSize, sourceCreateDate, sourceETag, localPath);

        if (replaceMode == ApprovalMode.RequireApproval)
        {
            existingItem.Status = SyncStatus.Pending;
            existingItem.PendingType = PendingType.Replacement;
            existingItem.StatusDate = DateTime.UtcNow;
            database.Upsert(existingItem);
            logger.LogInformation("Marked {FileName} for pending replacement (requires approval, ETag: {OldETag} -> {NewETag})",
                Path.GetFileName(sourcePath), oldETag ?? "null", sourceETag ?? "null");
            return new TransitionResult(true, "Pending replacement approval");
        }
        else
        {
            existingItem.Status = SyncStatus.Queued;
            existingItem.PendingType = null;
            existingItem.StatusDate = DateTime.UtcNow;
            database.Upsert(existingItem);
            logger.LogInformation("Re-queued {FileName} (source changed, ETag: {OldETag} -> {NewETag})",
                Path.GetFileName(sourcePath), oldETag ?? "null", sourceETag ?? "null");
            return new TransitionResult(true, "Re-queued for replacement");
        }
    }

    /// <summary>
    /// Verifies local file integrity for synced items.
    /// </summary>
    private static TransitionResult VerifyLocalFileIntegrity(
        SyncDatabase database,
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
                    // If replace is disabled, don't queue
                    if (replaceMode == ApprovalMode.Disabled)
                    {
                        return new TransitionResult(false, "Size mismatch but replace disabled");
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
                    database.Upsert(existingItem);
                    logger.LogInformation("Re-queued {FileName} (local size {LocalSize} != source size {SourceSize})",
                        Path.GetFileName(localPath), localInfo.Length, sourceSize);
                    return new TransitionResult(true, "Re-queued due to size mismatch");
                }
            }
            else
            {
                // Local file missing - queue for re-download
                existingItem.Status = SyncStatus.Queued;
                existingItem.PendingType = null;
                existingItem.StatusDate = DateTime.UtcNow;
                existingItem.LocalItemId = null;
                database.Upsert(existingItem);
                logger.LogInformation("Re-queued {FileName} (local file missing)", Path.GetFileName(localPath));
                return new TransitionResult(true, "Re-queued (local file missing)");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to check local file status for {LocalPath}", localPath);
        }

        return new TransitionResult(false, "No changes");
    }

    /// <summary>
    /// Checks if item metadata has changed based on the configured detection policy.
    /// Path is always checked as the primary identifier.
    /// </summary>
    private static bool HasMetadataChanged(
        SyncItem item,
        string sourcePath,
        long sourceSize,
        string? sourceETag,
        ChangeDetectionPolicy policy = ChangeDetectionPolicy.SizeOnly)
    {
        // Path is always checked as the primary identifier
        if (item.SourcePath != sourcePath)
        {
            return true;
        }

        // Check based on policy
        return policy switch
        {
            ChangeDetectionPolicy.SizeOnly => item.SourceSize != sourceSize,
            ChangeDetectionPolicy.ETagOnly => sourceETag != null && item.SourceETag != sourceETag,
            ChangeDetectionPolicy.Both => item.SourceSize != sourceSize ||
                                          (sourceETag != null && item.SourceETag != sourceETag),
            _ => item.SourceSize != sourceSize
        };
    }

    /// <summary>
    /// Updates item metadata fields.
    /// </summary>
    private static void UpdateItemMetadata(
        SyncItem item,
        string sourcePath,
        long sourceSize,
        DateTime sourceCreateDate,
        string? sourceETag,
        string localPath)
    {
        item.SourcePath = sourcePath;
        item.SourceSize = sourceSize;
        item.SourceCreateDate = sourceCreateDate;
        item.SourceETag = sourceETag;
        item.LocalPath = localPath;
    }
}
