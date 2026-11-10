using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// State rules for link rows, kept apart from <see cref="SyncStateService"/> so the copy rules are never
/// touched. A link has nothing to compare by size: it streams whatever the source holds, so a change on
/// the source only matters when it moves the file, and a link is settled once its .strm holds the
/// current URL.
/// </summary>
public static class LinkStateService
{
    /// <summary>
    /// Builds a row for a source file that is new here, or null when new files are switched off.
    /// The caller persists.
    /// </summary>
    public static SyncItem? ProcessNewLink(
        LibraryMapping mapping,
        string sourceItemId,
        string sourcePath,
        long sourceSize,
        DateTime sourceCreateDate,
        string linkPath,
        ApprovalMode downloadMode,
        LinkFiles files)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(files);
        if (downloadMode == ApprovalMode.Disabled)
        {
            return null;
        }

        var requiresApproval = downloadMode == ApprovalMode.RequireApproval;
        var row = new SyncItem
        {
            SourceLibraryId = mapping.SourceLibraryId,
            LocalLibraryId = mapping.LocalLibraryId,
            SourceItemId = sourceItemId,
            SourcePath = sourcePath,
            SourceSize = sourceSize,
            SourceCreateDate = sourceCreateDate,
            LocalPath = linkPath,
            StorageMode = StorageMode.Link,
            StatusDate = DateTime.UtcNow,
            Status = requiresApproval ? SyncStatus.Pending : SyncStatus.Queued,
            PendingType = requiresApproval ? PendingType.Download : null
        };

        if (files.IsCurrent(linkPath, sourceItemId))
        {
            row.Status = SyncStatus.Synced;
            row.PendingType = null;
        }

        return row;
    }

    /// <summary>
    /// Brings an existing link row up to date with the source and returns it. The caller persists.
    /// </summary>
    /// <param name="promoteMode">The new files approval when the mapping now copies, so the link is
    /// turned into a copy under that approval, or null when the mapping links.</param>
    public static SyncItem ProcessExistingLink(
        SyncItem row,
        string sourcePath,
        long sourceSize,
        DateTime sourceCreateDate,
        string linkPath,
        LinkFiles files,
        ILogger logger,
        ApprovalMode? promoteMode = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(logger);

        if (row.Status == SyncStatus.Ignored)
        {
            return row;
        }

        // Back on the source before its deletion ran: write it again rather than remove it.
        if (row.Status == SyncStatus.Deleting || (row.Status == SyncStatus.Pending && row.PendingType == PendingType.Deletion))
        {
            row.Status = SyncStatus.Queued;
            row.PendingType = null;
            row.StatusDate = DateTime.UtcNow;
        }

        // A rename on the source moves the .strm and its companions along, the same way a copy moves.
        SyncStateService.UpdateItemMetadata(row, sourcePath, sourceSize, sourceCreateDate, linkPath, logger);

        // The mapping now copies: the link becomes a copy, under the same approval a new file needs.
        if (row.StorageMode == StorageMode.Link && promoteMode is { } mode && mode != ApprovalMode.Disabled && row.Status != SyncStatus.Pending)
        {
            MarkPromoting(row, mode == ApprovalMode.RequireApproval);
            logger.LogDebug("Marked the link {FileName} to become a copy", Path.GetFileName(row.LocalPath));
            return row;
        }

        if (row.StorageMode == StorageMode.Promoting)
        {
            // Switched back to Link before anyone approved the copy: it stays a link.
            if (promoteMode is null && row.Status == SyncStatus.Pending)
            {
                row.StorageMode = StorageMode.Link;
                row.Status = SyncStatus.Queued;
                row.PendingType = null;
            }
            else if (row.Status == SyncStatus.Synced)
            {
                // Marked synced by hand while it waited: it stays a link for now, and under a Copy mapping
                // the next refresh asks for approval again rather than copying it unasked.
                row.StorageMode = StorageMode.Link;
                row.PendingType = null;
                return row;
            }
            else
            {
                return row;
            }
        }

        if (row.Status == SyncStatus.Pending)
        {
            return row;
        }

        var current = !string.IsNullOrEmpty(row.LocalPath) && files.IsCurrent(row.LocalPath, row.SourceItemId);
        if (current && row.Status is SyncStatus.Queued or SyncStatus.Errored)
        {
            row.Status = SyncStatus.Synced;
            row.PendingType = null;
            row.StatusDate = DateTime.UtcNow;
            row.Reason = null;
        }
        else if (row.Status == SyncStatus.Synced && (!current || !File.Exists(LinkNfo.PathFor(row.LocalPath!))))
        {
            // Missing, stale, or without its .nfo, such as a link from before links had one. Writing it
            // again is cheap, and leaves anything already right as it is.
            row.Status = SyncStatus.Queued;
            row.PendingType = null;
            row.StatusDate = DateTime.UtcNow;
            logger.LogDebug("Re-queued the link {FileName} (missing or out of date)", Path.GetFileName(row.LocalPath));
        }

        return row;
    }

    /// <summary>
    /// Marks a link to be turned into a full copy. It keeps playing as a link until the copy is in place.
    /// Returns false for a row that is not a link.
    /// </summary>
    public static bool MarkPromoting(SyncItem row, bool requireApproval)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.StorageMode.IsLink())
        {
            return false;
        }

        row.StorageMode = StorageMode.Promoting;
        row.Status = requireApproval ? SyncStatus.Pending : SyncStatus.Queued;
        row.PendingType = requireApproval ? PendingType.Replacement : null;
        row.StatusDate = DateTime.UtcNow;
        row.RetryCount = 0;
        return true;
    }
}
