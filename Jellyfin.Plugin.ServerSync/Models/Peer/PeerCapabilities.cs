using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// What a peer server running Server Sync can do, so the caller can fail with a clear message
/// instead of a bare 404 when the other side lacks the plugin or a feature.
/// </summary>
public class PeerCapabilities
{
    /// <summary>Gets or sets the peer's Jellyfin server id.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the peer's Server Sync version.</summary>
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the negotiation features the peer supports.</summary>
    public List<string> Features { get; set; } = new();

    /// <summary>
    /// Gets or sets the kinds of hint this server applies, by name: the modules that are on here. A
    /// sender only announces these kinds, so a module turned off on the receiver costs no traffic.
    /// Absent from older peers, which is read as every kind.
    /// </summary>
    public List<string>? Accepts { get; set; }

    /// <summary>
    /// Gets or sets the peer's clock at the time of the answer, in UTC. Conflicts between two edits made
    /// within the skew window are decided by these clocks, so Check Link reports a peer whose clock is off.
    /// </summary>
    public DateTime? ServerTime { get; set; }
}
