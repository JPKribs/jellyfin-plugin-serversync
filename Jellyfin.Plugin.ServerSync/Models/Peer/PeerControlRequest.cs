namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// A remote control command from the source's dashboard for a viewer of a link, sent to the server
/// that holds the link, which passes it to the viewer's client.
/// </summary>
public class PeerControlRequest
{
    /// <summary>Gets or sets the sending server's Jellyfin id.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the receiving server's session id for the viewer.</summary>
    public string SessionKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the playstate command, such as Stop, Pause, Unpause, or Seek. Null for a message.</summary>
    public string? Command { get; set; }

    /// <summary>Gets or sets where to seek to, in ticks.</summary>
    public long? SeekPositionTicks { get; set; }

    /// <summary>Gets or sets a message's header.</summary>
    public string? Header { get; set; }

    /// <summary>Gets or sets a message's text. A request with text and no command shows a message.</summary>
    public string? Text { get; set; }

    /// <summary>Gets or sets how long the message shows, in milliseconds.</summary>
    public long? TimeoutMs { get; set; }
}
