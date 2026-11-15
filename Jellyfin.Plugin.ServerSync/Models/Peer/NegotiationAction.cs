namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>What the sender does next after hearing back from the peer about one row.</summary>
public enum NegotiationAction
{
    /// <summary>The peer holds the merged state. Write it locally.</summary>
    Proceed,

    /// <summary>The row was merged again against the peer's live state. Offer it once more.</summary>
    Retry,

    /// <summary>The row cannot be settled this run. Error it with the reason.</summary>
    Fail
}
