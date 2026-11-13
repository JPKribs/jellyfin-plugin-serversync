namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>What happened to playback of a link, sent to the source so it shows there as a session.</summary>
public enum LinkPlaybackEvent
{
    /// <summary>Playback started.</summary>
    Start = 0,

    /// <summary>Playback went on, or paused or resumed.</summary>
    Progress = 1,

    /// <summary>Playback stopped.</summary>
    Stop = 2
}

/// <summary>One playback report for a link, from the server that holds the link to the source.</summary>
public class PeerPlaybackReport
{
    /// <summary>Gets or sets the reporting server's Jellyfin id.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets what happened.</summary>
    public LinkPlaybackEvent Event { get; set; }

    /// <summary>Gets or sets the source's id of the item.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the user's id on the reporting server.</summary>
    public string? UserId { get; set; }

    /// <summary>Gets or sets the source user the reporting server maps that user to, if it does.</summary>
    public string? SourceUserId { get; set; }

    /// <summary>Gets or sets the reporting server's session id, so each viewer there is a session of its own here.</summary>
    public string SessionKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the reporting server's play session id.</summary>
    public string? PlaySessionId { get; set; }

    /// <summary>Gets or sets the position, in ticks.</summary>
    public long? PositionTicks { get; set; }

    /// <summary>Gets or sets a value indicating whether playback is paused.</summary>
    public bool IsPaused { get; set; }

    /// <summary>Gets or sets how the client plays it: DirectPlay, DirectStream, or Transcode.</summary>
    public string? PlayMethod { get; set; }

    /// <summary>Gets or sets the name of the client playing it on the reporting server.</summary>
    public string? ClientName { get; set; }

    /// <summary>Gets or sets the user's name on the reporting server, which names the session on the source.</summary>
    public string? UserName { get; set; }
}

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
