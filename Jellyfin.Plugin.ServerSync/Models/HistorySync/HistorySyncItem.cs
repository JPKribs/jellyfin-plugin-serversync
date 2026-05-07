using System;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Services;

namespace Jellyfin.Plugin.ServerSync.Models.HistorySync;

/// <summary>
/// One row of synced watch-history state per (source user, source item). The
/// row carries three parallel value triples (Source / Local / Merged) for
/// each of the five primitives that make up watch state: <c>IsPlayed</c>,
/// <c>PlayCount</c>, <c>PlaybackPositionTicks</c>, <c>LastPlayedDate</c>,
/// <c>IsFavorite</c>. Refresh populates Source + Local and computes Merged
/// via <see cref="HistorySyncMergeService.MergeHistoryData"/>; Sync applies
/// Merged to the local server.
/// <para>
/// Primitives are stored directly (no <c>SyncableValue&lt;T&gt;</c> wrapper)
/// because there's no JSON-blob hashing benefit at this scale.
/// </para>
/// </summary>
public class HistorySyncItem : SyncRecord
{
    // ===== Mapping Context =====

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

    // ===== Item Identification =====

    /// <summary>
    /// Gets or sets the source server item ID — second component of the
    /// natural key.
    /// </summary>
    public string SourceItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local server item ID (resolved by path lookup).
    /// </summary>
    public string? LocalItemId { get; set; }

    /// <summary>
    /// Gets or sets the item name for display.
    /// </summary>
    public string? ItemName { get; set; }

    /// <summary>
    /// Gets or sets the source file path (for matching).
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Gets or sets the local file path (translated from source).
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

    /// <inheritdoc />
    public override bool HasChanges => HistorySyncMergeService.HasChangesToSync(this);

    /// <inheritdoc />
    public override void MarkSynced()
    {
        // No SyncableValue<T> fields on HistorySyncItem — primitives are
        // compared directly. Nothing to mark.
    }
}
