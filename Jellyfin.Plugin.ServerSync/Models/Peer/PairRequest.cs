namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// A peer hands this server the secret it must present on requests that name it as the sender. The
/// peer makes this call over its own connection to this server, so only a server that is configured
/// here and holds a key for this server can issue one.
/// </summary>
public class PairRequest
{
    /// <summary>Gets or sets the id of the server that issued the secret.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the secret to present to that server.</summary>
    public string Secret { get; set; } = string.Empty;
}
