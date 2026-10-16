namespace Jellyfin.Plugin.ServerSync.Models.Peer;

public class PeerLookupRequest
{
    public string? Path { get; set; }

    public string? ItemType { get; set; }

    public string? Name { get; set; }
}

public class PeerLookupResponse
{
    public bool Found { get; set; }

    public string? ItemId { get; set; }

    public string? ItemType { get; set; }
}
