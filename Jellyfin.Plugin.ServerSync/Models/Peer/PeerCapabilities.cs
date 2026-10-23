using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// What a peer server running Server Sync is, so the caller can fail with a clear message when the
/// other side lacks the plugin or runs a different version.
/// </summary>
public class PeerCapabilities
{
    /// <summary>Gets or sets the peer's Jellyfin server id.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the peer's Server Sync version.</summary>
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the wire protocol the peer speaks. Null from a peer that predates protocol
    /// versions, which is then only compatible with exactly its own plugin version.
    /// </summary>
    public int? ProtocolVersion { get; set; }

    /// <summary>Gets or sets the oldest wire protocol the peer still speaks.</summary>
    public int? MinProtocolVersion { get; set; }

    /// <summary>
    /// Gets or sets the kinds of hint this server applies, by name: the modules that are on here. A
    /// sender only announces these kinds, so a module turned off on the receiver costs no traffic.
    /// </summary>
    public List<string> Accepts { get; set; } = new();

    /// <summary>
    /// Gets or sets the peer's clock at the time of the answer, in UTC. Conflicts between two edits made
    /// within the skew window are decided by these clocks, so Check Link reports a peer whose clock is off.
    /// </summary>
    public DateTime? ServerTime { get; set; }
}
