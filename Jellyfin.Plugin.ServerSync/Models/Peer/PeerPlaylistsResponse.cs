using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// The playlists a peer offers.
/// </summary>
public class PeerPlaylistsResponse
{
    /// <summary>Gets or sets the playlists.</summary>
    public List<PeerPlaylist> Playlists { get; set; } = new();
}
