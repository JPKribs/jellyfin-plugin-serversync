using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>The copies a peer downloaded from other servers, answered by <c>GET /Sync/Peer/Copies</c>.</summary>
public class PeerCopiesResponse
{
    /// <summary>Gets or sets the copies.</summary>
    public List<PeerCopy> Copies { get; set; } = new();
}
