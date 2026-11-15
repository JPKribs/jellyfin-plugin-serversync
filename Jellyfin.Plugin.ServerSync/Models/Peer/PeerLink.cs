namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>One link on the peer, in the peer's own paths.</summary>
public class PeerLink
{
    /// <summary>Gets or sets where the .strm sits.</summary>
    public string LinkPath { get; set; } = string.Empty;

    /// <summary>Gets or sets where a full copy would sit, with the source file's real extension.</summary>
    public string CopiedPath { get; set; } = string.Empty;
}
