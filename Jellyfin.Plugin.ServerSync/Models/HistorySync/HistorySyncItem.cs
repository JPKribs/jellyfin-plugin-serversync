using System;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Common.Comparators;
using Jellyfin.Plugin.ServerSync.Services.HistorySync;

namespace Jellyfin.Plugin.ServerSync.Models.HistorySync;

/// <summary>
/// One row of History per source user and source item, with source, local, and merged values for each part.
/// The source values are also bundled into <see cref="SourceState"/>, whose hashes are stored with the row.
/// </summary>
public class HistorySyncItem : SyncRecord
{
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
    /// Gets or sets the source server item ID, second component of the
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

    /// <summary>
    /// Gets or sets the played state both servers last agreed on. Only recorded when history sync
    /// negotiates with the source, so one way installs keep their existing merge behavior.
    /// </summary>
    public bool? NegotiatedIsPlayed { get; set; }

    /// <summary>Gets or sets the play count both servers last agreed on.</summary>
    public int? NegotiatedPlayCount { get; set; }

    /// <summary>Gets or sets the resume position both servers last agreed on.</summary>
    public long? NegotiatedPlaybackPositionTicks { get; set; }

    /// <summary>Gets or sets the last played date both servers last agreed on.</summary>
    public DateTime? NegotiatedLastPlayedDate { get; set; }

    /// <summary>Gets or sets the favorite state both servers last agreed on.</summary>
    public bool? NegotiatedIsFavorite { get; set; }

    /// <summary>Gets or sets when the two servers last agreed, or null when they never have.</summary>
    public DateTime? NegotiatedAt { get; set; }

    /// <summary>Gets a value indicating whether a negotiated base exists for three way merging.</summary>
    public bool HasNegotiatedBase => NegotiatedAt.HasValue;

    /// <summary>
    /// Gets or sets a value indicating whether this row is offered to the source as well as written locally, so a stale source also counts as work.
    /// The scan sets it from the settings, and it is not stored.
    /// </summary>
    public bool NegotiateWithSource { get; set; }

    /// <summary>
    /// Records the merged values as the state both servers hold, once the source accepted them and the local write was verified.
    /// </summary>
    /// <param name="utcNow">The time of agreement.</param>
    public void RecordNegotiatedBase(DateTime utcNow)
    {
        NegotiatedIsPlayed = MergedIsPlayed;
        NegotiatedPlayCount = MergedPlayCount;
        NegotiatedPlaybackPositionTicks = MergedPlaybackPositionTicks;
        NegotiatedLastPlayedDate = MergedLastPlayedDate;
        NegotiatedIsFavorite = MergedIsFavorite;
        NegotiatedAt = utcNow;
    }

    /// <summary>Copies the negotiated base from a stored row.</summary>
    /// <param name="previous">The stored row whose base is kept.</param>
    public void CarryNegotiatedBaseFrom(HistorySyncItem previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        NegotiatedIsPlayed = previous.NegotiatedIsPlayed;
        NegotiatedPlayCount = previous.NegotiatedPlayCount;
        NegotiatedPlaybackPositionTicks = previous.NegotiatedPlaybackPositionTicks;
        NegotiatedLastPlayedDate = previous.NegotiatedLastPlayedDate;
        NegotiatedIsFavorite = previous.NegotiatedIsFavorite;
        NegotiatedAt = previous.NegotiatedAt;
    }

    /// <summary>
    /// Gets the source's values as one JSON blob, whose hashes record what was last applied and are never read to detect changes.
    /// </summary>
    public SyncableValue<string> SourceState { get; } = new SyncableValue<string>
    {
        Comparator = new JsonBlobComparator()
    };

    /// <summary>
    /// Builds <see cref="SourceState"/> again from the source values and updates its hash. A scan calls it after setting them.
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

    /// <summary>
    /// Gets a value indicating whether the merged values differ from what a server holds, which always compares values so playback on this server is never missed.
    /// </summary>
    public override bool HasChanges => HistorySyncMergeService.HasChangesToSync(this);

    /// <inheritdoc />
    public override void MarkSynced() => SourceState.MarkSynced();
}
