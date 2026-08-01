using System;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Common.Comparators;
using Jellyfin.Plugin.ServerSync.Services;

namespace Jellyfin.Plugin.ServerSync.Models.HistorySync;

/// <summary>
/// One row of synced watch-history state per (source user, source item),
/// carrying Source / Local / Merged triples for IsPlayed, PlayCount,
/// PlaybackPositionTicks, LastPlayedDate, and IsFavorite. The five
/// source-side primitives are also bundled into <see cref="SourceState"/>
/// to drive the SourceHash short-circuit on Refresh.
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

    // ===== Source-state bundle (drives the SourceHash short-circuit) =====

    /// <summary>
    /// Source-side watch state rolled into one JSON blob. <see cref="SourceHash"/>
    /// /<see cref="SyncedHash"/> on this value give us the fast-path skip
    /// on Refresh: if the source state hasn't moved since the last
    /// successful sync, the comparator never runs.
    /// </summary>
    public SyncableValue<string> SourceState { get; } = new SyncableValue<string>
    {
        Comparator = new JsonBlobComparator()
    };

    /// <summary>
    /// Recomputes the <see cref="SourceState"/> bundle from the current
    /// <c>Source*</c> fields and updates the source-hash. Call after
    /// populating <c>Source*</c> in the Refresh path so the hash short-circuit
    /// stays honest.
    /// </summary>
    public void UpdateSourceStateBundle()
    {
        var bundle = JsonSerializer.Serialize(new
        {
            SourceIsPlayed,
            SourcePlayCount,
            SourcePlaybackPositionTicks,
            SourceLastPlayedDate,
            SourceIsFavorite
        });
        SourceState.UpdateSource(bundle);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Always the merged-vs-local check. The old <c>SourceHash == SyncedHash</c>
    /// short-circuit asked whether the SOURCE had moved, so a local playback
    /// after a sync was never reconciled until the source item changed again.
    /// See <see cref="SyncableValue{T}.HasChanges"/> for the full reasoning.
    /// </remarks>
    public override bool HasChanges => HistorySyncMergeService.HasChangesToSync(this);

    /// <inheritdoc />
    public override void MarkSynced() => SourceState.MarkSynced();
}
