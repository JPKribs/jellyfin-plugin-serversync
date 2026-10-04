using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// A batch of proposed history writes sent to a peer server.
/// </summary>
public class PeerHistoryRequest
{
    /// <summary>Gets or sets the sending server's id, for the receiver's log.</summary>
    public string SenderServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the entries to negotiate.</summary>
    public List<PeerHistoryEntry> Items { get; set; } = new();
}
