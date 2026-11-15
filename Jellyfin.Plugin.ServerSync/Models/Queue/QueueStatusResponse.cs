using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Queue;

/// <summary>This server's inbound queue, as a peer sees it when checking on work it sent.</summary>
public class QueueStatusResponse
{
    /// <summary>Gets or sets this server's id.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the inbound rows.</summary>
    public List<InboundHintDto> Inbound { get; set; } = new();

    /// <summary>
    /// Gets or sets the hints finished recently, each with the stamp it applied.
    /// An origin closes a hint only when that stamp is at least its row's, so an older completion never closes a newer edit.
    /// </summary>
    public List<CompletedHint> CompletedHints { get; set; } = new();
}
