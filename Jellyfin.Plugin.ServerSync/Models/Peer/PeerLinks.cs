using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>A peer's links, answered by <c>GET /Sync/Peer/Links</c>.</summary>
public class PeerLinksResponse
{
    /// <summary>Gets or sets the links.</summary>
    public List<PeerLink> Links { get; set; } = new();
}

/// <summary>One link on the peer, in the peer's own paths.</summary>
public class PeerLink
{
    /// <summary>Gets or sets where the .strm sits.</summary>
    public string LinkPath { get; set; } = string.Empty;

    /// <summary>Gets or sets where a full copy would sit, with the source file's real extension.</summary>
    public string CopiedPath { get; set; } = string.Empty;
}
