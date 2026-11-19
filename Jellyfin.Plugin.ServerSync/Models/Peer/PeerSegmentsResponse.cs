using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// A peer's answer to a <see cref="PeerSegmentsRequest"/>, one entry for each item it named.
/// </summary>
public class PeerSegmentsResponse
{
    /// <summary>Gets the segments of each item, in the order the request named them.</summary>
    public List<PeerSegmentsItem> Items { get; init; } = new();
}
