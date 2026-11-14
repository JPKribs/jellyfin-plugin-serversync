using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>What left a peer's library since a time, answered by <c>GET /Sync/Peer/Changes</c>.</summary>
public class PeerChangesResponse
{
    /// <summary>Gets or sets the time the peer's record of removals reaches back to.</summary>
    public DateTime CoveredFrom { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the caller asked from before <see cref="CoveredFrom"/>,
    /// in which case removals were missed that this answer cannot name and only a full listing finds them.
    /// </summary>
    public bool TooOld { get; set; }

    /// <summary>Gets or sets the peer's ids of the media files removed since the time asked, without dashes.</summary>
    public List<string> RemovedItemIds { get; set; } = new();
}
