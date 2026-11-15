using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Queue;

/// <summary>A receiver telling an origin that hints are done.</summary>
public class CompleteRequest
{
    /// <summary>Gets or sets the reporting server's id.</summary>
    public string SenderServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the finished hints.</summary>
    public List<CompletedHint> Items { get; set; } = new();
}
