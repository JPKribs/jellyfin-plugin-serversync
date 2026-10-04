namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// The receiving server's answer for one proposed history write.
/// </summary>
public class PeerHistoryResult
{
    /// <summary>Gets or sets the receiving server's user id, echoed from the entry.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the receiving server's item id, echoed from the entry.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets what happened.</summary>
    public PeerHistoryOutcome Outcome { get; set; }

    /// <summary>Gets or sets a short explanation for a stale or failed outcome.</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Gets or sets the receiver's state after handling the entry. For a stale outcome this is the
    /// live state the sender did not know about, so it can merge again.
    /// </summary>
    public PeerHistoryState? Current { get; set; }
}
