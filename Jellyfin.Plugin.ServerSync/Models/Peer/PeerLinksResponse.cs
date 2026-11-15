using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>A peer's links, answered by <c>GET /Sync/Peer/Links</c>.</summary>
public class PeerLinksResponse
{
    /// <summary>Gets or sets the links.</summary>
    public List<PeerLink> Links { get; set; } = new();
}
