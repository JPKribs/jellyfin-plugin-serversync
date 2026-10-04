namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>What the sender does with a batch of hints after the peer's answer.</summary>
public enum DeliveryOutcome
{
    /// <summary>200. The peer holds the hints. Rows become sent and wait for completion.</summary>
    Accepted,

    /// <summary>400. The peer could not read the hints. Rows fail for good and are shown to the operator.</summary>
    Malformed,

    /// <summary>401, 403, 404, or 409. The peer cannot take hints from this server until something is fixed. The peer is paused with the reason.</summary>
    PausePeer,

    /// <summary>Anything else, including no answer. Rows are retried with backoff, forever.</summary>
    Retry
}

/// <summary>
/// Maps the peer's HTTP answer to what the sender does, in one place so the rule can be read and tested.
/// </summary>
public static class HintDelivery
{
    /// <summary>Classifies a status code. Zero means no answer at all.</summary>
    /// <param name="statusCode">The HTTP status, or zero.</param>
    /// <returns>The outcome.</returns>
    public static DeliveryOutcome Classify(int statusCode) => statusCode switch
    {
        200 => DeliveryOutcome.Accepted,
        400 => DeliveryOutcome.Malformed,
        401 or 403 or 404 or 409 or 428 => DeliveryOutcome.PausePeer,
        _ => DeliveryOutcome.Retry
    };

    /// <summary>
    /// Sorts the sent rows a peer was asked about. A hint the peer finished is completed here, a hint the
    /// peer still holds is left to wait, and a hint the peer neither holds nor finished was lost and is sent
    /// again.
    /// </summary>
    /// <param name="sentHintIds">The hint ids of the rows that waited past the grace period.</param>
    /// <param name="heldByPeer">The hint ids in the peer's inbound queue.</param>
    /// <param name="completedByPeer">The hint ids the peer finished recently.</param>
    /// <returns>The ids to complete and the ids to send again.</returns>
    public static (System.Collections.Generic.List<string> Complete, System.Collections.Generic.List<string> Resend) Reconcile(
        System.Collections.Generic.IEnumerable<string> sentHintIds,
        System.Collections.Generic.ISet<string> heldByPeer,
        System.Collections.Generic.ISet<string> completedByPeer)
    {
        System.ArgumentNullException.ThrowIfNull(sentHintIds);
        System.ArgumentNullException.ThrowIfNull(heldByPeer);
        System.ArgumentNullException.ThrowIfNull(completedByPeer);
        var complete = new System.Collections.Generic.List<string>();
        var resend = new System.Collections.Generic.List<string>();
        foreach (var id in sentHintIds)
        {
            if (completedByPeer.Contains(id))
            {
                complete.Add(id);
            }
            else if (!heldByPeer.Contains(id))
            {
                resend.Add(id);
            }
        }

        return (complete, resend);
    }

    /// <summary>The reason shown for a paused peer, by status.</summary>
    /// <param name="statusCode">The HTTP status.</param>
    /// <param name="body">The peer's answer body, trimmed.</param>
    /// <returns>The reason.</returns>
    public static string PauseReason(int statusCode, string body) => statusCode switch
    {
        401 or 403 => "the peer refused this server's key. Push and Sync need an administrator's key for the peer. Check the key on this server entry",
        404 => "Server Sync is not installed on the peer, or its version predates hints",
        428 => "the peer could not pair with this server. It has to reach this server at the URL on its entry for it: " + body,
        _ => "the peer does not list this server as a source: " + body
    };
}
