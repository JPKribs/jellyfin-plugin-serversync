namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// Whether this server lists the asking server, and how. A server about to send hints asks this
/// before it queues anything, since a hint is only accepted from a server listed as Pull or Sync.
/// </summary>
public class PeerLinkResponse
{
    /// <summary>Gets or sets this server's id.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets this server's name.</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the asking server is listed here at all.</summary>
    public bool Listed { get; set; }

    /// <summary>Gets or sets the mode the asking server is listed with, or null.</summary>
    public string? Mode { get; set; }

    /// <summary>Gets or sets a value indicating whether the listing is enabled, with a URL and a key.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether this server pulls from the asking server, which is what accepting its hints needs.</summary>
    public bool PullsFromYou { get; set; }

    /// <summary>Gets or sets a value indicating whether this server sends hints to the asking server.</summary>
    public bool SendsToYou { get; set; }
}
