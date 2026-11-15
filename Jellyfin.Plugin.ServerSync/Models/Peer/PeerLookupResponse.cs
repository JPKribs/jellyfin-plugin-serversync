namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// The item a peer found for a <see cref="PeerLookupRequest"/>.
/// </summary>
public class PeerLookupResponse
{
    /// <summary>Gets or sets a value indicating whether the peer found a match.</summary>
    public bool Found { get; set; }

    /// <summary>Gets or sets the matched item's id on the peer.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets the matched item's Jellyfin type.</summary>
    public string? ItemType { get; set; }
}
