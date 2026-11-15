namespace Jellyfin.Plugin.ServerSync.Models.Queue;

/// <summary>Where an outbound row is in its life.</summary>
public enum OutboundState
{
    /// <summary>Waiting to be delivered, or waiting for its next attempt.</summary>
    Pending = 0,

    /// <summary>The peer answered 200 and holds the hint. The row waits for the peer to report it done.</summary>
    Sent = 1,

    /// <summary>
    /// The peer refused the hint as malformed. It is not retried, and the operator is shown it.
    /// </summary>
    Failed = 2
}
