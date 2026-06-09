using Jellyfin.Plugin.ServerSync.Models.HistorySync;

namespace Jellyfin.Plugin.ServerSync.Models;

/// <summary>
/// Mapping helpers for <see cref="HistorySyncItem"/>.
/// </summary>
public static class HistorySyncItemExtensions
{
    /// <summary>Projects a history sync item to its API DTO.</summary>
    /// <param name="item">The history sync item.</param>
    /// <param name="sourceServerUrl">The source server URL surfaced to the client.</param>
    /// <param name="sourceServerApiKey">The source server API key surfaced to the client.</param>
    /// <returns>The DTO representation.</returns>
    public static HistorySyncItemDto ToDto(this HistorySyncItem item, string? sourceServerUrl, string? sourceServerApiKey)
    {
        return new HistorySyncItemDto
        {
            Id = item.Id,
            SourceUserId = item.SourceUserId,
            LocalUserId = item.LocalUserId,
            SourceLibraryId = item.SourceLibraryId,
            LocalLibraryId = item.LocalLibraryId,
            SourceItemId = item.SourceItemId,
            LocalItemId = item.LocalItemId,
            ItemName = item.ItemName,
            SourcePath = item.SourcePath,
            LocalPath = item.LocalPath,
            SourceIsPlayed = item.SourceIsPlayed,
            SourcePlayCount = item.SourcePlayCount,
            SourcePlaybackPositionTicks = item.SourcePlaybackPositionTicks,
            SourceLastPlayedDate = item.SourceLastPlayedDate,
            SourceIsFavorite = item.SourceIsFavorite,
            LocalIsPlayed = item.LocalIsPlayed,
            LocalPlayCount = item.LocalPlayCount,
            LocalPlaybackPositionTicks = item.LocalPlaybackPositionTicks,
            LocalLastPlayedDate = item.LocalLastPlayedDate,
            LocalIsFavorite = item.LocalIsFavorite,
            MergedIsPlayed = item.MergedIsPlayed,
            MergedPlayCount = item.MergedPlayCount,
            MergedPlaybackPositionTicks = item.MergedPlaybackPositionTicks,
            MergedLastPlayedDate = item.MergedLastPlayedDate,
            MergedIsFavorite = item.MergedIsFavorite,
            SourceServerUrl = sourceServerUrl,
            SourceServerApiKey = sourceServerApiKey,
            Status = item.Status.ToString(),
            StatusDate = item.StatusDate,
            LastSyncTime = item.LastSyncTime,
            ErrorMessage = item.Reason,
            HasChanges = item.HasChanges
        };
    }
}
