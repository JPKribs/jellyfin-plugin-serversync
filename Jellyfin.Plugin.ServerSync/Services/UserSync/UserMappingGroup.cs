using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.UserSync;

namespace Jellyfin.Plugin.ServerSync.Services.UserSync;

/// <summary>
/// One user mapping with the rows of each of its categories, shown as one row on the Users sync table.
/// </summary>
public sealed class UserMappingGroup
{
    /// <summary>
    /// Gets the user's id on the source.
    /// </summary>
    public required string SourceUserId { get; init; }

    /// <summary>
    /// Gets the user's id on this server.
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
    /// Gets the mapping's rows, one for each of Policy, Configuration, and ProfileImage that is tracked.
    /// </summary>
    public required IList<UserSyncItem> Items { get; init; }
}
