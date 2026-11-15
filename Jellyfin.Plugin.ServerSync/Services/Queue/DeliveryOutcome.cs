namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>What the sender does with a batch of hints after the peer's answer.</summary>
public enum DeliveryOutcome
{
    /// <summary>The peer answered 200 and holds the hints, so the rows become sent and wait for completion.</summary>
    Accepted,

    /// <summary>The peer answered 400 because it could not read the hints, so the rows fail for good and are shown to the operator.</summary>
    Malformed,

    /// <summary>The peer answered 401, 403, 404, 409, 426, or 428 and cannot take hints until something is fixed, so it is paused with the reason.</summary>
    PausePeer,

    /// <summary>The peer answered anything else or not at all, so the rows are retried with backoff.</summary>
    Retry
}
