using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using JPKribs.Jellyfin.Base;
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
    /// <param name="mapping">The library mapping the file came through.</param>
    /// <param name="sourceItemId">The file's id on the source.</param>
    /// <param name="sourcePath">The file's path on the source.</param>
    /// <param name="sourceSize">The file's size on the source, in bytes.</param>
    /// <param name="sourceCreateDate">When the file was created on the source.</param>
    /// <param name="linkPath">Where the link sits here.</param>
    /// <param name="downloadMode">Whether new files are added on their own, wait for approval, or are left alone.</param>
    /// <param name="files">Tells whether the link's .strm already holds the current URL.</param>
    /// <returns>The new row, or null.</returns>
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
    /// <param name="row">The link's row, which is changed in place.</param>
    /// <param name="sourcePath">The file's path on the source.</param>
    /// <param name="sourceSize">The file's size on the source, in bytes.</param>
    /// <param name="sourceCreateDate">When the file was created on the source.</param>
    /// <param name="linkPath">Where the link sits here.</param>
    /// <param name="files">Tells whether the link's .strm already holds the current URL.</param>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="promoteMode">The approval a new file needs when the library mapping copies, or null when it links.</param>
    /// <returns>The updated row.</returns>
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

        // Back on the source before its deletion ran, so it is written again.
        if (row.Status == SyncStatus.Deleting || (row.Status == SyncStatus.Pending && row.PendingType == PendingType.Deletion))
        {
            row.Status = SyncStatus.Queued;
            row.PendingType = null;
            row.StatusDate = DateTime.UtcNow;
        }

        var replaced = row.StorageMode == StorageMode.Link && row.Status == SyncStatus.Synced
            && row.SourceSize > 0 && sourceSize > 0 && row.SourceSize != sourceSize;

        SyncStateService.UpdateItemMetadata(row, sourcePath, sourceSize, sourceCreateDate, linkPath, logger);

        // The library mapping copies, so the link becomes a copy under approval.
        if (row.StorageMode == StorageMode.Link && promoteMode is { } mode && mode != ApprovalMode.Disabled && row.Status != SyncStatus.Pending)
        {
            MarkPromoting(row, mode == ApprovalMode.RequireApproval);
            logger.LogDebug("Marked the link {FileName} to become a copy", FormatUtilities.SanitizeForLog(Path.GetFileName(row.LocalPath)));
            return row;
        }

        if (row.StorageMode == StorageMode.Promoting)
        {
            // Switched back to Link before the copy was approved, so it stays a link.
            if (promoteMode is null && row.Status == SyncStatus.Pending)
            {
                row.StorageMode = StorageMode.Link;
                row.Status = SyncStatus.Queued;
                row.PendingType = null;
            }
            else if (row.Status == SyncStatus.Synced)
            {
                // Marked synced by hand while waiting, so the next refresh asks again.
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

        // A file replaced on the source needs its subtitles and media info taken again.
        if (replaced && row.Status == SyncStatus.Synced)
        {
            row.Status = SyncStatus.Queued;
            row.PendingType = null;
            row.StatusDate = DateTime.UtcNow;
            logger.LogDebug("Re-queued the link {FileName} (the file changed on the source)", FormatUtilities.SanitizeForLog(Path.GetFileName(row.LocalPath)));
            return row;
        }

        var current = !string.IsNullOrEmpty(row.LocalPath) && files.IsCurrent(row.LocalPath, row.SourceItemId);
        var hasNfo = !string.IsNullOrEmpty(row.LocalPath) && File.Exists(LinkNfo.PathFor(row.LocalPath));

        // Recording this plugin's .nfo makes a later removal count as the user's choice.
        if (hasNfo && !LinkNfo.Recorded(row) && LinkNfo.IsOurs(LinkNfo.PathFor(row.LocalPath!)))
        {
            LinkNfo.Record(row);
        }

        // A link flagged by the link check is settled only by that check.
        if (current && row.Status is SyncStatus.Queued or SyncStatus.Errored && !LinkVerifier.Flagged(row))
        {
            row.Status = SyncStatus.Synced;
            row.PendingType = null;
            row.StatusDate = DateTime.UtcNow;
            row.Reason = null;
        }
        else if (row.Status == SyncStatus.Synced && (!current || (!hasNfo && !LinkNfo.Recorded(row))))
        {
            // Missing, stale, or never given its .nfo, and writing it again is cheap.
            row.Status = SyncStatus.Queued;
            row.PendingType = null;
            row.StatusDate = DateTime.UtcNow;
            logger.LogDebug("Re-queued the link {FileName} (missing or out of date)", FormatUtilities.SanitizeForLog(Path.GetFileName(row.LocalPath)));
        }

        return row;
    }

    /// <summary>
    /// Marks a link to be turned into a full copy, or returns <c>false</c> for a row that is not a link.
    /// It keeps playing as a link until the copy is in place.
    /// </summary>
    /// <param name="row">The row of the link to turn into a copy.</param>
    /// <param name="requireApproval"><c>true</c> when the copy waits for approval.</param>
    /// <returns><c>true</c> when the row was marked.</returns>
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
