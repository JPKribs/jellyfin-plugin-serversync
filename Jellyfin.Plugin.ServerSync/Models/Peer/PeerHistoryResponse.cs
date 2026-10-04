using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// The receiving server's answers for a batch of proposed history writes.
/// </summary>
public class PeerHistoryResponse
{
    /// <summary>Gets or sets one result per entry, in request order.</summary>
    public List<PeerHistoryResult> Items { get; set; } = new();
}
