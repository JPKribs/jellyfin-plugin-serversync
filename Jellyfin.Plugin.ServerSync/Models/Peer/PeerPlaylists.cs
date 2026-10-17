using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

public class PeerPlaylistsResponse
{
    public List<PeerPlaylist> Playlists { get; set; } = new();
}

public class PeerPlaylist
{
    public string Id { get; set; } = string.Empty;

    public string RootId { get; set; } = string.Empty;

    public bool IsCopy { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? MediaType { get; set; }

    public string OwnerUserId { get; set; } = string.Empty;

    public bool OpenAccess { get; set; }

    public List<PeerPlaylistShare> Shares { get; set; } = new();

    public List<PeerPlaylistEntry> Entries { get; set; } = new();
}

public class PeerPlaylistShare
{
    public string UserId { get; set; } = string.Empty;

    public bool CanEdit { get; set; }
}

public class PeerPlaylistEntry
{
    public string ItemId { get; set; } = string.Empty;

    public string? Path { get; set; }
}
