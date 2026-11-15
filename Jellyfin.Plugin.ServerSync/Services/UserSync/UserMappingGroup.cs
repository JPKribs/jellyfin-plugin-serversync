using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.UserSync;

namespace Jellyfin.Plugin.ServerSync.Services.UserSync;

/// <summary>
/// One user mapping with its grouped category items, returned by
/// <see cref="UserSyncTableManager.PaginateUserMappings"/>. The user-grouping
/// UI displays all three categories (Policy, Configuration, ProfileImage)
/// per user as one logical row. This DTO is that shape.
/// </summary>
public sealed class UserMappingGroup
{
    /// <summary>
    /// Gets the source server user ID.
    /// </summary>
    public required string SourceUserId { get; init; }

    /// <summary>
    /// Gets the local server user ID.
    /// </summary>
    public required string LocalUserId { get; init; }

    /// <summary>
    /// Gets the source user's display name.
    /// </summary>
    public string? SourceUserName { get; init; }

    /// <summary>
    /// Gets the local user's display name.
    /// </summary>
    public string? LocalUserName { get; init; }

    /// <summary>
    /// Gets all category items for this mapping (typically up to 3:
    /// Policy, Configuration, ProfileImage).
    /// </summary>
    public required IList<UserSyncItem> Items { get; init; }
}
