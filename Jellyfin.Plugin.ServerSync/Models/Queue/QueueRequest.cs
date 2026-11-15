using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Queue;

/// <summary>A batch of hints offered to a peer.</summary>
public class QueueRequest
{
    /// <summary>Gets or sets the sending server's id. Every hint in the batch originates there.</summary>
    public string SenderServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the hints.</summary>
    public List<SyncHint> Items { get; set; } = new();
}
