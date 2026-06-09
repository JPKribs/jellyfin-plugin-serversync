using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Utilities;

namespace Jellyfin.Plugin.ServerSync.Models;

/// <summary>
/// Mapping helpers for <see cref="UserSyncItem"/>.
/// </summary>
public static class UserSyncItemExtensions
{
    /// <summary>Projects a user sync item to its API DTO.</summary>
    /// <param name="item">The user sync item.</param>
    /// <param name="sourceServerUrl">The source server URL surfaced to the client.</param>
    /// <param name="sourceServerApiKey">The source server API key surfaced to the client.</param>
    /// <returns>The DTO representation.</returns>
    public static UserSyncItemDto ToDto(this UserSyncItem item, string? sourceServerUrl = null, string? sourceServerApiKey = null)
    {
        return new UserSyncItemDto
        {
            Id = item.Id,
            SourceUserId = item.SourceUserId,
            LocalUserId = item.LocalUserId,
            SourceUserName = item.SourceUserName,
            LocalUserName = item.LocalUserName,
            PropertyCategory = item.PropertyCategory,
            SourceValue = item.SourceValue,
            LocalValue = item.LocalValue,
            MergedValue = item.MergedValue,
            SourceImageSize = item.SourceImageSize,
            LocalImageSize = item.LocalImageSize,
            SourceImageSizeFormatted = item.SourceImageSize.HasValue ? FormatUtilities.FormatBytes(item.SourceImageSize.Value) : null,
            LocalImageSizeFormatted = item.LocalImageSize.HasValue ? FormatUtilities.FormatBytes(item.LocalImageSize.Value) : null,
            HasChanges = item.HasChanges,
            ChangesSummary = item.ChangesSummary,
            SourceServerUrl = sourceServerUrl,
            SourceServerApiKey = sourceServerApiKey,
            Status = item.Status.ToString(),
            StatusDate = item.StatusDate,
            LastSyncTime = item.LastSyncTime,
            ErrorMessage = item.Reason
        };
    }
}
