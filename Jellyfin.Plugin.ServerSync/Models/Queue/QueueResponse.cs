using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Queue;

/// <summary>The receiver's answers for a batch of hints.</summary>
public class QueueResponse
{
    /// <summary>Gets or sets one result per hint, in request order.</summary>
    public List<QueueResult> Items { get; set; } = new();
}
