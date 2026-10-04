using System;
using Jellyfin.Plugin.ServerSync.Models.Peer;

namespace Jellyfin.Plugin.ServerSync.Services.Peer;

/// <summary>
/// The thin seam the peer history service reads and writes through, so the compare and set logic
/// can be tested against an in memory store instead of a live Jellyfin library.
/// </summary>
public interface IUserHistoryStore
{
    /// <summary>Reads a user's history for an item, or null when the user or item does not exist.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    /// <returns>The current state, or null.</returns>
    PeerHistoryState? Read(Guid userId, Guid itemId);

    /// <summary>Writes a state. Null fields are left as they are, except that an unplayed state clears the date.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="state">The state to write.</param>
    /// <returns><c>true</c> when the write succeeded.</returns>
    bool Write(Guid userId, Guid itemId, PeerHistoryState state);
}
