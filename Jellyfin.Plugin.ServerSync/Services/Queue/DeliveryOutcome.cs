namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>What the sender does with a batch of hints after the peer's answer.</summary>
public enum DeliveryOutcome
{
    /// <summary>200. The peer holds the hints. Rows become sent and wait for completion.</summary>
    Accepted,

    /// <summary>400. The peer could not read the hints. Rows fail for good and are shown to the operator.</summary>
    Malformed,

    /// <summary>401, 403, 404, 409, 426, or 428. The peer cannot take hints from this server until something is fixed. The peer is paused with the reason.</summary>
    PausePeer,

    /// <summary>Anything else, including no answer. Rows are retried with backoff, forever.</summary>
    Retry
}
