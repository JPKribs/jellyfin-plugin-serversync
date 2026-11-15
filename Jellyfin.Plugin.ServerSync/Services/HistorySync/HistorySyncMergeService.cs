using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;

namespace Jellyfin.Plugin.ServerSync.Services.HistorySync;

/// <summary>
/// Merges history from the source and this server, against the agreed base once the two have agreed on one.
/// </summary>
public static class HistorySyncMergeService
{
    /// <summary>
    /// Merges source and local history into the merged state, then clears every part that is turned off.
    /// </summary>
    /// <param name="item">The history row whose merged values are set.</param>
    public static void MergeHistoryData(HistorySyncItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.HasNegotiatedBase)
        {
            MergeAgainstBase(item);
            KeepEnabledFields(item);
            return;
        }

        // Before any agreed base, a source win would wipe favorites held only here.
        if (item.NegotiateWithSource && (item.SourceIsFavorite.HasValue || item.LocalIsFavorite.HasValue))
        {
            item.MergedIsFavorite = (item.SourceIsFavorite ?? false) || (item.LocalIsFavorite ?? false);
        }
        else if (item.SourceIsFavorite.HasValue)
        {
            item.MergedIsFavorite = item.SourceIsFavorite.Value;
        }
        else
        {
            // A source with no reading must not wipe a local favorite.
            item.MergedIsFavorite = item.LocalIsFavorite;
        }

        item.MergedPlayCount = Math.Max(item.SourcePlayCount ?? 0, item.LocalPlayCount ?? 0);
        MergeNegotiatedHistory(item);
        KeepEnabledFields(item);
    }

    /// <summary>
    /// Gets or sets the reader of which parts of history the History settings allow to sync.
    /// </summary>
    public static Func<HistoryFields> EnabledFields { get; set; } = static () => HistoryFields.All;

    /// <summary>
    /// The parts held for the calling flow by <see cref="WithFields"/>.
    /// </summary>
    private static readonly AsyncLocal<HistoryFields?> ScopedFields = new();

    /// <summary>
    /// Gets the parts of history that may sync for the calling flow.
    /// </summary>
    /// <returns>The enabled parts.</returns>
    public static HistoryFields CurrentFields() => ScopedFields.Value ?? EnabledFields();

    /// <summary>
    /// Holds the enabled parts to a given set for the calling flow until the result is disposed, so one piece of work sees one answer.
    /// </summary>
    /// <param name="fields">The parts to sync while the scope is held.</param>
    /// <returns>Restores the previous answer when disposed.</returns>
    public static IDisposable WithFields(HistoryFields fields)
    {
        var before = ScopedFields.Value;
        ScopedFields.Value = fields;
        return new FieldScope(before);
    }

    /// <summary>
    /// Restores the previous scoped parts when disposed.
    /// </summary>
    private sealed class FieldScope : IDisposable
    {
        private readonly HistoryFields? _before;

        /// <summary>
        /// Initializes a new instance of the <see cref="FieldScope"/> class.
        /// </summary>
        /// <param name="before">The scoped parts to restore on dispose.</param>
        public FieldScope(HistoryFields? before) => _before = before;

        /// <inheritdoc/>
        public void Dispose() => ScopedFields.Value = _before;
    }

    /// <summary>
    /// Clears the merged value of every part that is turned off, so it is written nowhere and compared with nothing.
    /// </summary>
    /// <param name="item">The history row whose merged values are cleared.</param>
    public static void KeepEnabledFields(HistorySyncItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var fields = CurrentFields();
        if (!fields.HasFlag(HistoryFields.Played))
        {
            item.MergedIsPlayed = null;
        }

        if (!fields.HasFlag(HistoryFields.Position))
        {
            item.MergedPlaybackPositionTicks = null;
        }

        if (!fields.HasFlag(HistoryFields.PlayCount))
        {
            item.MergedPlayCount = null;
        }

        if (!fields.HasFlag(HistoryFields.LastPlayed))
        {
            item.MergedLastPlayedDate = null;
        }

        if (!fields.HasFlag(HistoryFields.Favorite))
        {
            item.MergedIsFavorite = null;
        }
    }

    /// <summary>
    /// Merges each part against the agreed base, so a side that still matches the base loses to a side that moved.
    /// A null on a side is no reading, not a change, and the first merge rules break a tie when both sides moved.
    /// </summary>
    private static void MergeAgainstBase(HistorySyncItem item)
    {
        var sourceFavoriteMoved = item.SourceIsFavorite.HasValue && item.SourceIsFavorite != item.NegotiatedIsFavorite;
        var localFavoriteMoved = item.LocalIsFavorite.HasValue && item.LocalIsFavorite != item.NegotiatedIsFavorite;
        if (sourceFavoriteMoved && localFavoriteMoved)
        {
            item.MergedIsFavorite = item.SourceIsFavorite!.Value || item.LocalIsFavorite!.Value;
        }
        else if (sourceFavoriteMoved)
        {
            item.MergedIsFavorite = item.SourceIsFavorite;
        }
        else if (localFavoriteMoved)
        {
            item.MergedIsFavorite = item.LocalIsFavorite;
        }
        else
        {
            item.MergedIsFavorite = item.NegotiatedIsFavorite;
        }

        var sourceCountMoved = item.SourcePlayCount.HasValue && item.SourcePlayCount != item.NegotiatedPlayCount;
        var localCountMoved = item.LocalPlayCount.HasValue && item.LocalPlayCount != item.NegotiatedPlayCount;
        if (sourceCountMoved && localCountMoved)
        {
            item.MergedPlayCount = Math.Max(item.SourcePlayCount!.Value, item.LocalPlayCount!.Value);
        }
        else if (sourceCountMoved)
        {
            item.MergedPlayCount = item.SourcePlayCount;
        }
        else if (localCountMoved)
        {
            item.MergedPlayCount = item.LocalPlayCount;
        }
        else
        {
            item.MergedPlayCount = item.NegotiatedPlayCount ?? 0;
        }

        var fields = CurrentFields();
        bool? Mark(bool? played) => fields.HasFlag(HistoryFields.Played) ? played : null;
        long? Spot(long? position) => fields.HasFlag(HistoryFields.Position) ? position : null;
        DateTime? Date(DateTime? lastPlayed) => fields.HasFlag(HistoryFields.LastPlayed) ? lastPlayed : null;

        var sourcePlayMoved = PlayGroupMoved(
            Mark(item.SourceIsPlayed), Spot(item.SourcePlaybackPositionTicks), Date(item.SourceLastPlayedDate),
            Mark(item.NegotiatedIsPlayed), Spot(item.NegotiatedPlaybackPositionTicks), Date(item.NegotiatedLastPlayedDate));
        var localPlayMoved = PlayGroupMoved(
            Mark(item.LocalIsPlayed), Spot(item.LocalPlaybackPositionTicks), Date(item.LocalLastPlayedDate),
            Mark(item.NegotiatedIsPlayed), Spot(item.NegotiatedPlaybackPositionTicks), Date(item.NegotiatedLastPlayedDate));

        if (sourcePlayMoved && localPlayMoved)
        {
            MergeNegotiatedHistory(item);
        }
        else if (sourcePlayMoved)
        {
            item.MergedIsPlayed = item.SourceIsPlayed;
            item.MergedPlaybackPositionTicks = item.SourcePlaybackPositionTicks;
            item.MergedLastPlayedDate = item.SourceLastPlayedDate;
        }
        else if (localPlayMoved)
        {
            item.MergedIsPlayed = item.LocalIsPlayed;
            item.MergedPlaybackPositionTicks = item.LocalPlaybackPositionTicks;
            item.MergedLastPlayedDate = item.LocalLastPlayedDate;
        }
        else
        {
            item.MergedIsPlayed = item.NegotiatedIsPlayed ?? item.SourceIsPlayed ?? item.LocalIsPlayed;
            item.MergedPlaybackPositionTicks = item.NegotiatedPlaybackPositionTicks ?? item.SourcePlaybackPositionTicks ?? item.LocalPlaybackPositionTicks;
            item.MergedLastPlayedDate = item.NegotiatedLastPlayedDate ?? item.SourceLastPlayedDate ?? item.LocalLastPlayedDate;
        }
    }

    /// <summary>
    /// Whether one side's played group differs from the agreed base, with dates compared to the second.
    /// Callers pass null for a part that is turned off, since it has no agreed value and would read as moved on both sides.
    /// </summary>
    private static bool PlayGroupMoved(
        bool? played, long? position, DateTime? lastPlayed,
        bool? basePlayed, long? basePosition, DateTime? baseLastPlayed)
    {
        if (!played.HasValue && !position.HasValue && !lastPlayed.HasValue)
        {
            return false;
        }

        if (played.HasValue && played != basePlayed)
        {
            return true;
        }

        if (position.HasValue && position != basePosition)
        {
            return true;
        }

        if (lastPlayed.HasValue)
        {
            return !SameInstantToSecond(lastPlayed, baseLastPlayed);
        }

        // Clearing the date is how Jellyfin marks an item unplayed.
        return baseLastPlayed.HasValue && played.HasValue;
    }

    /// <summary>
    /// Takes the played mark, position, and last played date from the side played most recently.
    /// </summary>
    private static void MergeNegotiatedHistory(HistorySyncItem item)
    {
        var sourceDate = item.SourceLastPlayedDate;
        var localDate = item.LocalLastPlayedDate;

        if (!sourceDate.HasValue && !localDate.HasValue)
        {
            item.MergedIsPlayed = item.SourceIsPlayed ?? item.LocalIsPlayed;
            item.MergedLastPlayedDate = null;
            item.MergedPlaybackPositionTicks = item.SourcePlaybackPositionTicks ?? item.LocalPlaybackPositionTicks;
        }
        else if (sourceDate.HasValue && !localDate.HasValue)
        {
            item.MergedIsPlayed = item.SourceIsPlayed;
            item.MergedLastPlayedDate = sourceDate;
            item.MergedPlaybackPositionTicks = item.SourcePlaybackPositionTicks;
        }
        else if (!sourceDate.HasValue)
        {
            item.MergedIsPlayed = item.LocalIsPlayed;
            item.MergedLastPlayedDate = localDate;
            item.MergedPlaybackPositionTicks = item.LocalPlaybackPositionTicks;
        }
        else if (sourceDate.Value >= localDate!.Value)
        {
            item.MergedIsPlayed = item.SourceIsPlayed;
            item.MergedLastPlayedDate = sourceDate;
            item.MergedPlaybackPositionTicks = item.SourcePlaybackPositionTicks;
        }
        else
        {
            item.MergedIsPlayed = item.LocalIsPlayed;
            item.MergedLastPlayedDate = localDate;
            item.MergedPlaybackPositionTicks = item.LocalPlaybackPositionTicks;
        }
    }

    /// <summary>
    /// Determines whether the merged state asks for a write on this server or on a Sync source.
    /// </summary>
    /// <param name="item">The history row whose merged and local values are compared.</param>
    /// <returns><c>true</c> when there is a change to sync.</returns>
    public static bool HasChangesToSync(HistorySyncItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.MergedIsPlayed != item.LocalIsPlayed && item.MergedIsPlayed.HasValue)
        {
            return true;
        }

        if (item.MergedPlayCount != item.LocalPlayCount && item.MergedPlayCount.HasValue && item.MergedPlayCount > 0)
        {
            return true;
        }

        if (item.MergedPlaybackPositionTicks != item.LocalPlaybackPositionTicks && item.MergedPlaybackPositionTicks.HasValue)
        {
            return true;
        }

        if (item.MergedIsFavorite != item.LocalIsFavorite && item.MergedIsFavorite.HasValue)
        {
            return true;
        }

        // Jellyfin drops fractions of a second, so an exact compare would requeue forever.
        if (item.MergedLastPlayedDate.HasValue
            && !SameInstantToSecond(item.MergedLastPlayedDate, item.LocalLastPlayedDate))
        {
            return true;
        }

        // Jellyfin records an unplayed mark by clearing the date, so a local date must go.
        if (item.MergedIsPlayed == false && !item.MergedLastPlayedDate.HasValue && item.LocalLastPlayedDate.HasValue)
        {
            return true;
        }

        // A change made only on this server must still reach a Sync source.
        return item.NegotiateWithSource && SourceDiffersFromMerged(item);
    }

    /// <summary>
    /// Determines whether the source's last read state differs from the merged state in any part the source has a reading for.
    /// </summary>
    /// <param name="item">The history row whose merged and source values are compared.</param>
    /// <returns><c>true</c> when the source would need a write to hold the merged state.</returns>
    public static bool SourceDiffersFromMerged(HistorySyncItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.MergedIsPlayed.HasValue && item.SourceIsPlayed.HasValue && item.MergedIsPlayed != item.SourceIsPlayed)
        {
            return true;
        }

        if (item.MergedPlayCount.HasValue && item.SourcePlayCount.HasValue && item.MergedPlayCount != item.SourcePlayCount)
        {
            return true;
        }

        if (item.MergedPlaybackPositionTicks.HasValue && item.SourcePlaybackPositionTicks.HasValue
            && item.MergedPlaybackPositionTicks != item.SourcePlaybackPositionTicks)
        {
            return true;
        }

        if (item.MergedIsFavorite.HasValue && item.SourceIsFavorite.HasValue && item.MergedIsFavorite != item.SourceIsFavorite)
        {
            return true;
        }

        // With the date turned off, only an unplayed mark asks the source to drop its date.
        if (!CurrentFields().HasFlag(HistoryFields.LastPlayed))
        {
            return item.MergedIsPlayed == false && item.SourceLastPlayedDate.HasValue;
        }

        return item.SourceIsPlayed.HasValue && !SameInstantToSecond(item.MergedLastPlayedDate, item.SourceLastPlayedDate);
    }

    /// <summary>
    /// Compares two timestamps to the second, the one definition of the same instant for history sync.
    /// </summary>
    /// <param name="a">The first timestamp, or null for no date.</param>
    /// <param name="b">The second timestamp, or null for no date.</param>
    /// <returns><c>true</c> when both are null or both land in the same second.</returns>
    public static bool SameInstantToSecond(DateTime? a, DateTime? b)
    {
        if (!a.HasValue && !b.HasValue)
        {
            return true;
        }

        if (!a.HasValue || !b.HasValue)
        {
            return false;
        }

        return TruncateToSecond(a.Value) == TruncateToSecond(b.Value);
    }

    /// <summary>
    /// Drops sub-second precision, preserving <see cref="DateTime.Kind"/>.
    /// </summary>
    /// <param name="value">The timestamp to cut down to whole seconds.</param>
    /// <returns>The timestamp with milliseconds and ticks removed.</returns>
    public static DateTime TruncateToSecond(DateTime value)
        => new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.Kind);

    /// <summary>
    /// Describes the changes the merged state makes to this server, for logs and the dashboard.
    /// </summary>
    /// <param name="item">The history row whose local and merged values are compared.</param>
    /// <returns>A readable list of changes, or "No changes".</returns>
    public static string GetChangeSummary(HistorySyncItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var changes = new List<string>();

        if (item.MergedIsPlayed != item.LocalIsPlayed && item.MergedIsPlayed.HasValue)
        {
            changes.Add($"Played: {item.LocalIsPlayed ?? false} -> {item.MergedIsPlayed.Value}");
        }

        if (item.MergedPlayCount != item.LocalPlayCount && item.MergedPlayCount.HasValue)
        {
            changes.Add($"PlayCount: {item.LocalPlayCount ?? 0} -> {item.MergedPlayCount.Value}");
        }

        if (item.MergedPlaybackPositionTicks != item.LocalPlaybackPositionTicks && item.MergedPlaybackPositionTicks.HasValue)
        {
            var localPos = TimeSpan.FromTicks(item.LocalPlaybackPositionTicks ?? 0);
            var mergedPos = TimeSpan.FromTicks(item.MergedPlaybackPositionTicks.Value);
            changes.Add($"Position: {localPos:hh\\:mm\\:ss} -> {mergedPos:hh\\:mm\\:ss}");
        }

        if (item.MergedIsFavorite != item.LocalIsFavorite && item.MergedIsFavorite.HasValue)
        {
            changes.Add($"Favorite: {item.LocalIsFavorite ?? false} -> {item.MergedIsFavorite.Value}");
        }

        return changes.Count > 0 ? string.Join(", ", changes) : "No changes";
    }
}
