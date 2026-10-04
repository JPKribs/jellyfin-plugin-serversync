namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// What the receiving server did with one proposed history write.
/// </summary>
public enum PeerHistoryOutcome
{
    /// <summary>The proposal was written and verified.</summary>
    Applied,

    /// <summary>The receiver already held the proposed state, so nothing was written.</summary>
    Unchanged,

    /// <summary>The receiver's live state no longer matched the sender's expectation. Nothing was written.</summary>
    Stale,

    /// <summary>The user or item does not exist on the receiver.</summary>
    NotFound,

    /// <summary>The write or its verification failed. See the reason.</summary>
    Failed
}
