using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// Asks a peer for the media segments of a batch of its items.
/// </summary>
public class PeerSegmentsRequest
{
    /// <summary>The most items one request may name.</summary>
    public const int MaxItems = 200;

    /// <summary>Gets the peer's ids of the items whose segments are read.</summary>
    public List<string> ItemIds { get; init; } = new();
}
