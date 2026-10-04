namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// One proposed history write for the receiving server. The ids are the receiving server's own ids,
/// since the sender learned them when it read this user's history from the receiver.
/// </summary>
public class PeerHistoryEntry
{
    /// <summary>Gets or sets the receiving server's user id.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the receiving server's item id.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the state the sender believes the receiver currently holds. The receiver only writes
    /// when its live state still matches, so a change made on the receiver after the sender read it is
    /// never overwritten blind.
    /// </summary>
    public PeerHistoryState? Expected { get; set; }

    /// <summary>Gets or sets the merged state the sender wants both servers to hold.</summary>
    public PeerHistoryState Proposed { get; set; } = new();

    /// <summary>
    /// Gets or sets the sender's own user id for this entry, so the receiver can keep its own history
    /// row for the sender up to date with what the two just agreed on. Optional.
    /// </summary>
    public string? SenderUserId { get; set; }

    /// <summary>Gets or sets the sender's own item id for this entry. Optional.</summary>
    public string? SenderItemId { get; set; }
}
