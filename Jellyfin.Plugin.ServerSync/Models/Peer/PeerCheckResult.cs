using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// What the settings page learns about a server entry before Push or Sync mode is relied on.
/// </summary>
public class PeerCheckResult
{
    /// <summary>Gets or sets a value indicating whether the server answered at all.</summary>
    public bool Reachable { get; set; }

    /// <summary>Gets or sets the server's name.</summary>
    public string? ServerName { get; set; }

    /// <summary>Gets or sets the server's id.</summary>
    public string? ServerId { get; set; }

    /// <summary>Gets or sets a value indicating whether Server Sync is installed there.</summary>
    public bool HasPlugin { get; set; }

    /// <summary>Gets or sets its Server Sync version.</summary>
    public string? PluginVersion { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether its Server Sync and this one speak protocols that do not
    /// overlap, so neither takes the other's changes until one is updated.
    /// </summary>
    public bool Incompatible { get; set; }

    /// <summary>Gets or sets a value indicating whether it lists this server as Pull or Sync, which hints need.</summary>
    public bool ListsThisServer { get; set; }

    /// <summary>Gets or sets the direction it lists this server with, or null.</summary>
    public string? PeerMode { get; set; }

    /// <summary>Gets or sets a value indicating whether it sends hints back to this server.</summary>
    public bool SendsToThisServer { get; set; }

    /// <summary>Gets or sets the kinds the peer applies, by name, or null when it is too old to say.</summary>
    public List<string>? Accepts { get; set; }

    /// <summary>Gets or sets how many seconds the peer's clock differs from this server's, when it could be read.</summary>
    public int ClockSkewSeconds { get; set; }

    /// <summary>Gets or sets a value indicating whether the peer issued this server a pairing secret during the check.</summary>
    public bool Paired { get; set; }

    /// <summary>Gets or sets why the peer could not pair, when it could not.</summary>
    public string? PairingError { get; set; }

    /// <summary>Gets or sets the access level the other server's key for this server has there.</summary>
    public string? PeerAccessLevel { get; set; }

    /// <summary>Gets or sets the user that key belongs to there, when it is a standard user's.</summary>
    public string? PeerAccessUser { get; set; }

    /// <summary>Gets or sets a value indicating whether that key may download media there, or null when not known.</summary>
    public bool? PeerCanDownload { get; set; }

    /// <summary>Gets or sets a short verdict for the page, shown above the details.</summary>
    public string Headline { get; set; } = string.Empty;

    /// <summary>Gets or sets what was learned, one line each. Empty when the check ended before the link could be judged.</summary>
    public List<PeerCheckDetail> Details { get; set; } = new();

    /// <summary>Gets or sets the whole answer as sentences.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets how the page shows the message: ok, warn, or error.</summary>
    public string Severity { get; set; } = "error";
}
