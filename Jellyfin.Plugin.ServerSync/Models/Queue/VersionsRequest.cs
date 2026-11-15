using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Queue;

/// <summary>Asks a peer for the versions it holds for a batch of its own keys.</summary>
public class VersionsRequest
{
    /// <summary>Gets or sets the kind.</summary>
    public HintKind Kind { get; set; }

    /// <summary>Gets or sets the peer's keys.</summary>
    public List<string> Keys { get; set; } = new();
}
