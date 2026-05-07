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
        // IsFavorite: Always taken from Source Server
        item.MergedIsFavorite = item.SourceIsFavorite ?? false;

        // PlayCount: Take the maximum of Source and Local
        item.MergedPlayCount = Math.Max(item.SourcePlayCount ?? 0, item.LocalPlayCount ?? 0);

        // Played, Position, and LastPlayedDate: Negotiated based on most recent LastPlayedDate
        MergeNegotiatedHistory(item);
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

        if (item.MergedLastPlayedDate != item.LocalLastPlayedDate && item.MergedLastPlayedDate.HasValue)
        {
            return true;
        }

        // Also check if we have source data but no local data was matched yet
        if (string.IsNullOrEmpty(item.LocalItemId) && !string.IsNullOrEmpty(item.SourceItemId))
        {
            // Item exists on source but hasn't been matched locally
            return false; // Can't sync without local item
        }

        return false;
    }

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
