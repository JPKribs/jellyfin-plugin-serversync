using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Services.Playlists;

/// <summary>A playlist as another server holds it.</summary>
/// <param name="Id">The playlist's id there.</param>
/// <param name="RootId">The id of the playlist it was first made as.</param>
/// <param name="Name">The playlist's name.</param>
/// <param name="MediaType">The playlist's media type, or null.</param>
/// <param name="OwnerUserId">The owner's id there.</param>
/// <param name="OpenAccess">Whether any user may open the playlist.</param>
/// <param name="Shares">The users it is shared with, and whether each may edit.</param>
/// <param name="EntryPaths">The path of each entry, in order.</param>
/// <param name="OwnerKnown"><c>true</c> when the owner is known exactly, not assumed from the key in use.</param>
/// <param name="IsCopy"><c>true</c> when the playlist is itself a copy of another.</param>
public sealed record SourcePlaylist(
    string Id,
    string RootId,
    string Name,
    string? MediaType,
    string OwnerUserId,
    bool OpenAccess,
    List<(string UserId, bool CanEdit)> Shares,
    List<string?> EntryPaths,
    bool OwnerKnown,
    bool IsCopy)
{
    /// <summary>Gets a value indicating whether the playlist is the original and not a copy.</summary>
    public bool IsOriginal => !IsCopy;
}
