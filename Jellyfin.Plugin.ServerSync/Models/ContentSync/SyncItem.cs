using System;
using Jellyfin.Plugin.ServerSync.Models.Common;

namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Tracks one media file's sync state. The natural key is
/// <see cref="SourceItemId"/>. Status is the standard lifecycle (Pending /
/// Queued / Synced / Errored / Ignored / Deleting), and <see cref="PendingType"/>
/// describes the operation (Download / Replacement / Deletion) that's
/// awaiting approval or queued.
/// </summary>
public class SyncItem : SyncRecord
{
    /// <summary>
    /// Gets or sets the source library ID.
    /// </summary>
    public string SourceLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local library ID.
    /// </summary>
    public string LocalLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source server's item ID, natural key.
    /// </summary>
    public string SourceItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source file path.
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source file size in bytes, primary change-detection signal.
    /// </summary>
    public long SourceSize { get; set; }

    /// <summary>
    /// Gets or sets the source file's create date.
    /// </summary>
    public DateTime SourceCreateDate { get; set; }

    /// <summary>
    /// Gets or sets the local item's Jellyfin ID once resolved.
    /// </summary>
    public string? LocalItemId { get; set; }

    /// <summary>
    /// Gets or sets the local file path (translated from source via library mapping).
    /// </summary>
    public string? LocalPath { get; set; }

    /// <summary>
    /// Gets or sets the type of pending operation (Download / Replacement / Deletion).
    /// Null when the item is Synced or Errored.
    /// </summary>
    public PendingType? PendingType { get; set; }

    /// <summary>
    /// Gets or sets the comma-separated list of companion file names that
    /// were downloaded with this item (subtitles, NFO, etc.).
    /// </summary>
    public string? CompanionFiles { get; set; }

    /// <summary>
    /// Gets or sets how the file is held here. A link row's <see cref="LocalPath"/> is the .strm, and its
    /// <see cref="SourcePath"/> keeps the source file's real path and extension.
    /// </summary>
    public StorageMode StorageMode { get; set; }

    // Content sync's "needs work" signal is the PendingType, not a hash
    // compare. Refresh sets PendingType when the file needs to be
    // downloaded, replaced, or deleted, so this is true while the row is
    // pending or queued and false once Synced.

    /// <inheritdoc />
    public override bool HasChanges
        => Status == SyncStatus.Queued
            || Status == SyncStatus.Deleting
            || (Status == SyncStatus.Pending && PendingType.HasValue);

    /// <inheritdoc />
    public override void MarkSynced()
    {
        // No SyncableValue<T> fields on Content, nothing to mark.
    }
}
