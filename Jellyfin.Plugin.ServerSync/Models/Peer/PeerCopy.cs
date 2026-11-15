namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>One downloaded copy on the peer, in the peer's own path.</summary>
public class PeerCopy
{
    /// <summary>Gets or sets where the file sits on the peer.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the id of the server the file first came from.</summary>
    public string OriginServerId { get; set; } = string.Empty;
}
