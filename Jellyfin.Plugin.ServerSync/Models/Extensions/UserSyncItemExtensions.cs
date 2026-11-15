using System;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using JPKribs.Jellyfin.Base;

namespace Jellyfin.Plugin.ServerSync.Models.Extensions;

/// <summary>
/// Mapping helpers for <see cref="UserSyncItem"/>.
/// </summary>
public static class UserSyncItemExtensions
{
    /// <summary>Projects a user sync item to its API DTO.</summary>
    /// <param name="item">The row to show.</param>
    /// <param name="sourceServerUrl">The source's URL the browser loads images and links from.</param>
    /// <returns>The row as the dashboard reads it.</returns>
    public static UserSyncItemDto ToDto(this UserSyncItem item, string? sourceServerUrl = null)
    {
        ArgumentNullException.ThrowIfNull(item);

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
            ServerKey = item.ServerKey,
            Status = item.Status.ToString(),
            StatusDate = item.StatusDate,
            LastSyncTime = item.LastSyncTime,
            ErrorMessage = item.Reason
        };
    }
}
