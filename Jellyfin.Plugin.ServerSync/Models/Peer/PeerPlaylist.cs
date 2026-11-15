using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// One playlist a peer offers, with its shares and items.
/// </summary>
public class PeerPlaylist
{
    /// <summary>Gets or sets the playlist's id on the peer.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the id of the original playlist this one is, or is a copy of.</summary>
    public string RootId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the playlist is a copy of another server's.</summary>
    public bool IsCopy { get; set; }

    /// <summary>Gets or sets the playlist's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the playlist's media type, such as Video or Audio.</summary>
    public string? MediaType { get; set; }

    /// <summary>Gets or sets the id of the user who owns the playlist.</summary>
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether every user may see the playlist.</summary>
    public bool OpenAccess { get; set; }

    /// <summary>Gets or sets the users the playlist is shared with.</summary>
    public List<PeerPlaylistShare> Shares { get; set; } = new();

    /// <summary>Gets or sets the playlist's items, in order.</summary>
    public List<PeerPlaylistEntry> Entries { get; set; } = new();
}
