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

    /// <summary>Gets or sets a value indicating whether its Server Sync accepts hints.</summary>
    public bool SupportsHints { get; set; }

    /// <summary>Gets or sets a value indicating whether it lists this server as Pull or Sync, which hints need.</summary>
    public bool ListsThisServer { get; set; }

    /// <summary>Gets or sets the mode it lists this server with, or null.</summary>
    public string? PeerMode { get; set; }

    /// <summary>Gets or sets a value indicating whether it sends hints back to this server.</summary>
    public bool SendsToThisServer { get; set; }

    /// <summary>Gets or sets the kinds the peer applies, by name, or null when it is too old to say.</summary>
    public System.Collections.Generic.List<string>? Accepts { get; set; }

    /// <summary>Gets or sets how many seconds the peer's clock differs from this server's, when it could be read.</summary>
    public int ClockSkewSeconds { get; set; }

    /// <summary>Gets or sets a sentence for the page.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets how the page shows the message: ok, warn, or error.</summary>
    public string Severity { get; set; } = "error";
}
