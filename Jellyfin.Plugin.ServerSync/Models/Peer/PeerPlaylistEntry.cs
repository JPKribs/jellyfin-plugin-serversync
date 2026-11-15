namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// One item in a playlist a peer offers.
/// </summary>
public class PeerPlaylistEntry
{
    /// <summary>Gets or sets the item's id on the peer.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the item's path on the peer, used to find it here.</summary>
    public string? Path { get; set; }
}
