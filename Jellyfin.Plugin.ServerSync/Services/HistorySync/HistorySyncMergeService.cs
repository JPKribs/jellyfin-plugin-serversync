using System;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Service for merging watch history data from source and local servers.
/// Implements merge logic with the following strategy:
/// - IsFavorite: Always taken from Source Server
/// - PlayCount: MAX(source, local)
/// - Played, Position, LastPlayedDate: Negotiated based on most recent LastPlayedDate
/// </summary>
public static class HistorySyncMergeService
{
    /// <summary>
    /// Merges source and local history data to produce the target merged state.
    /// Merge strategy:
    /// - IsFavorite: Always from Source Server
    /// - PlayCount: MAX(source, local)
    /// - Played, Position, LastPlayedDate: From whichever server has more recent LastPlayedDate
    /// </summary>
    /// <param name="item">History sync item to update with merged values.</param>
    public static void MergeHistoryData(HistorySyncItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.HasNegotiatedBase)
        {
            MergeAgainstBase(item);
            return;
        }

        // IsFavorite: take source when it has an opinion, otherwise keep
        // local. The previous unconditional `?? false` conflated two cases
        // for a null source value — "explicitly unfavorited" and "UserData
        // not available in the response" — silently wiping local favorites
        // on the latter.
        if (item.NegotiateWithSource && (item.SourceIsFavorite.HasValue || item.LocalIsFavorite.HasValue))
        {
            // Before the servers have agreed once there is no way to tell which side changed, and
            // letting the source win would wipe every favorite that exists only here on the first
            // two way run. Keeping both sides' favorites is the only safe first merge.
            item.MergedIsFavorite = (item.SourceIsFavorite ?? false) || (item.LocalIsFavorite ?? false);
        }
        else if (item.SourceIsFavorite.HasValue)
        {
            item.MergedIsFavorite = item.SourceIsFavorite.Value;
        }
        else
        {
            item.MergedIsFavorite = item.LocalIsFavorite;
        }

        // PlayCount: Take the maximum of Source and Local
        item.MergedPlayCount = Math.Max(item.SourcePlayCount ?? 0, item.LocalPlayCount ?? 0);

        // Played, Position, and LastPlayedDate: Negotiated based on most recent LastPlayedDate
        MergeNegotiatedHistory(item);
    }

    /// <summary>
    /// Three way merge used once the two servers have agreed on a state at least once. Each field, or
    /// the played group as a whole, is compared against that agreed base. A side that still matches
    /// the base has not moved and loses to a side that has, which is what lets a favorite removed or an
    /// item marked unplayed on either server reach the other. When both sides moved the old rules
    /// break the tie. A null on a side is treated as "no reading" rather than a change.
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
            item.MergedIsFavorite = item.NegotiatedIsFavorite ?? item.SourceIsFavorite ?? item.LocalIsFavorite;
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
            item.MergedPlayCount = item.NegotiatedPlayCount ?? Math.Max(item.SourcePlayCount ?? 0, item.LocalPlayCount ?? 0);
        }

        var sourcePlayMoved = PlayGroupMoved(
            item.SourceIsPlayed, item.SourcePlaybackPositionTicks, item.SourceLastPlayedDate,
            item.NegotiatedIsPlayed, item.NegotiatedPlaybackPositionTicks, item.NegotiatedLastPlayedDate);
        var localPlayMoved = PlayGroupMoved(
            item.LocalIsPlayed, item.LocalPlaybackPositionTicks, item.LocalLastPlayedDate,
            item.NegotiatedIsPlayed, item.NegotiatedPlaybackPositionTicks, item.NegotiatedLastPlayedDate);

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
    /// Whether one side's played group differs from the agreed base. A side with no readings at all
    /// has not moved. A side with readings is compared field by field, with dates at second resolution.
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

        // A side that has a date differing from the base moved. A side whose date is null while the
        // base has one also moved, since clearing the date is how Jellyfin marks an item unplayed.
        if (lastPlayed.HasValue)
        {
            return !SameInstantToSecond(lastPlayed, baseLastPlayed);
        }

        return baseLastPlayed.HasValue && played.HasValue;
    }

    /// <summary>
    /// Merges negotiated history fields (Played, Position, LastPlayedDate) based on
    /// which server has the more recent LastPlayedDate.
    /// </summary>
    private static void MergeNegotiatedHistory(HistorySyncItem item)
    {
        var sourceDate = item.SourceLastPlayedDate;
        var localDate = item.LocalLastPlayedDate;

        // If neither has a date, use source values if available
        if (!sourceDate.HasValue && !localDate.HasValue)
        {
            item.MergedIsPlayed = item.SourceIsPlayed ?? item.LocalIsPlayed;
            item.MergedLastPlayedDate = null;
            item.MergedPlaybackPositionTicks = item.SourcePlaybackPositionTicks ?? item.LocalPlaybackPositionTicks;
        }
        else if (sourceDate.HasValue && !localDate.HasValue)
        {
            // Only source has a date, use source values
            item.MergedIsPlayed = item.SourceIsPlayed;
            item.MergedLastPlayedDate = sourceDate;
            item.MergedPlaybackPositionTicks = item.SourcePlaybackPositionTicks;
        }
        else if (!sourceDate.HasValue)
        {
            // Only local has a date, use local values
            item.MergedIsPlayed = item.LocalIsPlayed;
            item.MergedLastPlayedDate = localDate;
            item.MergedPlaybackPositionTicks = item.LocalPlaybackPositionTicks;
        }
        else if (sourceDate.Value >= localDate!.Value)
        {
            // Both have dates - source was more recently played, use source values
            item.MergedIsPlayed = item.SourceIsPlayed;
            item.MergedLastPlayedDate = sourceDate;
            item.MergedPlaybackPositionTicks = item.SourcePlaybackPositionTicks;
        }
        else
        {
            // Both have dates - local was more recently played, use local values
            item.MergedIsPlayed = item.LocalIsPlayed;
            item.MergedLastPlayedDate = localDate;
            item.MergedPlaybackPositionTicks = item.LocalPlaybackPositionTicks;
        }
    }

    /// <summary>
    /// Determines if there are meaningful changes to sync for this item.
    /// </summary>
    /// <param name="item">History sync item to check.</param>
    /// <returns>True if there are changes that need to be synced to local.</returns>
    public static bool HasChangesToSync(HistorySyncItem item)
    {
        // Check if merged values differ from local values
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

        // Second resolution, not exact equality. Jellyfin does not round-trip
        // sub-second precision through its user-data store, so an exact
        // comparison reports a difference on the very value we just wrote and
        // the row requeues on every run forever. The post-apply verifier has
        // always truncated to seconds for this reason; the change detector has
        // to agree with it, or "changed" and "applied correctly" contradict.
        // Previously masked by the SourceHash short-circuit, which stopped this
        // check from running at all once a row had been synced.
        if (item.MergedLastPlayedDate.HasValue
            && !SameInstantToSecond(item.MergedLastPlayedDate, item.LocalLastPlayedDate))
        {
            return true;
        }

        // An item merged to unplayed with no date must lose a lingering local date, since that is
        // how Jellyfin itself records a mark as unplayed.
        if (item.MergedIsPlayed == false && !item.MergedLastPlayedDate.HasValue && item.LocalLastPlayedDate.HasValue)
        {
            return true;
        }

        // In two way mode the source can be the side that is behind. Without this, a change made
        // only on this server merges to the local value, looks like "nothing to do" here, and never
        // reaches the source.
        return item.NegotiateWithSource && SourceDiffersFromMerged(item);
    }

    /// <summary>
    /// Whether the source's last read state differs from the merged state in any field the source has
    /// a reading for.
    /// </summary>
    /// <param name="item">History sync item to check.</param>
    /// <returns>True when the source would need a write to hold the merged state.</returns>
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

        // Only compare dates when the source has a reading of the item at all. A source with a
        // played flag but no date really has no date, so a merged date counts as a difference.
        return item.SourceIsPlayed.HasValue && !SameInstantToSecond(item.MergedLastPlayedDate, item.SourceLastPlayedDate);
    }

    /// <summary>
    /// Compares two nullable timestamps at second resolution. The single
    /// definition of "the same instant" for history sync — used by both the
    /// change detector and the post-apply verifier so they can never disagree.
    /// </summary>
    /// <param name="a">First timestamp.</param>
    /// <param name="b">Second timestamp.</param>
    /// <returns>True when both are null or both land in the same second.</returns>
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
    /// <param name="value">Timestamp to truncate.</param>
    /// <returns>The timestamp with milliseconds and ticks removed.</returns>
    public static DateTime TruncateToSecond(DateTime value)
        => new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.Kind);

    /// <summary>
    /// Creates a summary of changes for logging/display purposes.
    /// </summary>
    /// <param name="item">History sync item.</param>
    /// <returns>Human-readable summary of changes.</returns>
    public static string GetChangeSummary(HistorySyncItem item)
    {
        var changes = new System.Collections.Generic.List<string>();

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
