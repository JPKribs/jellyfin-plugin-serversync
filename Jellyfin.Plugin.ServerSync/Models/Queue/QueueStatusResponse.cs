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
    /// Gets or sets the hints finished recently with the version each one applied. An origin only treats
    /// a hint as done when the finished version is at least the one its row now carries, so a hint
    /// re-sent with a newer edit is never closed by an older completion.
    /// </summary>
    public List<CompletedHint> CompletedHints { get; set; } = new();
}
