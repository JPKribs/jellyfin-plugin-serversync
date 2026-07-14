using System;

namespace Jellyfin.Plugin.ServerSync.Models.HistorySync;

/// <summary>
/// History sync item information for API responses.
/// </summary>
public class HistorySyncItemDto
{
    /// <summary>
    /// Gets or sets the database ID.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the source server user ID.
    /// </summary>
    public string SourceUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local server user ID.
    /// </summary>
    public string LocalUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source library ID.
    /// </summary>
    public string SourceLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local library ID.
    /// </summary>
    public string LocalLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source item ID.
    /// </summary>
    public string SourceItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local item ID (if matched).
    /// </summary>
    public string? LocalItemId { get; set; }

    /// <summary>
    /// Gets or sets the item name for display.
    /// </summary>
    public string? ItemName { get; set; }

    /// <summary>
    /// Gets or sets the source file path.
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Gets or sets the local file path.
    /// </summary>
    public string? LocalPath { get; set; }

    // ===== Source History State =====

    /// <summary>
    /// Gets or sets whether the item is marked as played on source server.
    /// </summary>
    public bool? SourceIsPlayed { get; set; }

    /// <summary>
    /// Gets or sets the play count on source server.
    /// </summary>
    public int? SourcePlayCount { get; set; }

    /// <summary>
    /// Gets or sets the playback position in ticks on source server.
    /// </summary>
    public long? SourcePlaybackPositionTicks { get; set; }

    /// <summary>
    /// Gets or sets the last played date on source server.
    /// </summary>
    public DateTime? SourceLastPlayedDate { get; set; }

    /// <summary>
    /// Gets or sets whether the item is a favorite on source server.
    /// </summary>
    public bool? SourceIsFavorite { get; set; }

    // ===== Local History State =====

    /// <summary>
    /// Gets or sets whether the item is marked as played locally.
    /// </summary>
    public bool? LocalIsPlayed { get; set; }

    /// <summary>
    /// Gets or sets the play count locally.
    /// </summary>
    public int? LocalPlayCount { get; set; }

    /// <summary>
    /// Gets or sets the playback position in ticks locally.
    /// </summary>
    public long? LocalPlaybackPositionTicks { get; set; }

    /// <summary>
    /// Gets or sets the last played date locally.
    /// </summary>
    public DateTime? LocalLastPlayedDate { get; set; }

    /// <summary>
    /// Gets or sets whether the item is a favorite locally.
    /// </summary>
    public bool? LocalIsFavorite { get; set; }

    // ===== Merged/Target State =====

    /// <summary>
    /// Gets or sets the merged played status to apply.
    /// </summary>
    public bool? MergedIsPlayed { get; set; }

    /// <summary>
    /// Gets or sets the merged play count to apply.
    /// </summary>
    public int? MergedPlayCount { get; set; }

    /// <summary>
    /// Gets or sets the merged playback position to apply.
    /// </summary>
    public long? MergedPlaybackPositionTicks { get; set; }

    /// <summary>
    /// Gets or sets the merged last played date to apply.
    /// </summary>
    public DateTime? MergedLastPlayedDate { get; set; }

    /// <summary>
    /// Gets or sets the merged favorite status to apply.
    /// </summary>
    public bool? MergedIsFavorite { get; set; }

    // ===== Source Server =====

    /// <summary>
    /// Gets or sets the source server URL (for image display).
    /// </summary>
    public string? SourceServerUrl { get; set; }

    // ===== Sync Tracking =====

    /// <summary>
    /// Gets or sets the sync status.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the status was last changed.
    /// </summary>
    public DateTime StatusDate { get; set; }

    /// <summary>
    /// Gets or sets when the item was last synced.
    /// </summary>
    public DateTime? LastSyncTime { get; set; }

    /// <summary>
    /// Gets or sets the error message if status is Errored.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets whether there are changes to sync.
    /// </summary>
    public bool HasChanges { get; set; }
}
