using System;
using Jellyfin.Plugin.ServerSync.Models.Peer;

namespace Jellyfin.Plugin.ServerSync.Services.Peer;

/// <summary>
/// Reads and writes user history for the peer history service, so its compare and set rule can run against a test store.
/// </summary>
public interface IUserHistoryStore
{
    /// <summary>Reads a user's history for an item.</summary>
    /// <param name="userId">The local user whose history is read.</param>
    /// <param name="itemId">The local item the history belongs to.</param>
    /// <returns>The current state, or null when the user or item does not exist.</returns>
    PeerHistoryState? Read(Guid userId, Guid itemId);

    /// <summary>Writes a state, leaving null fields as they are except that an unplayed state clears the date.</summary>
    /// <param name="userId">The local user whose history is written.</param>
    /// <param name="itemId">The local item the history belongs to.</param>
    /// <param name="state">The state to write.</param>
    /// <returns><c>true</c> when the write succeeded.</returns>
    bool Write(Guid userId, Guid itemId, PeerHistoryState state);
}
