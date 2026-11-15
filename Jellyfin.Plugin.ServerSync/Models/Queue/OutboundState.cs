namespace Jellyfin.Plugin.ServerSync.Models.Queue;

/// <summary>Where an outbound row is in its life.</summary>
public enum OutboundState
{
    /// <summary>Waiting to be delivered, or waiting for its next attempt.</summary>
    Pending = 0,

    /// <summary>The peer answered 200 and holds the hint. The row waits for the peer to report it done.</summary>
    Sent = 1,

    /// <summary>The peer rejected the hint as malformed. Nothing will retry it. Shown to the operator.</summary>
    Failed = 2
}
