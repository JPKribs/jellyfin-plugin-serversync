using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// One configured peer server. The position of an entry in <see cref="Configuration.PluginConfiguration.Servers"/>
/// is its scan priority: when two servers offer the same item, the earlier entry wins and the later one only
/// contributes what the earlier lacks.
/// </summary>
public class SourceServer
{
    /// <summary>
    /// Gets or sets the stable key of this entry. Generated once when the entry is created and carried on
    /// every sync table row that came from it, so reordering or renaming the entry never orphans rows.
    /// </summary>
    public string Key { get; set; } = NewKey();

    /// <summary>Gets or sets the admin facing name. Falls back to the server's reported name, then its URL.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the URL this server connects to.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional public URL used only for image display in the dashboard, for peers reached over
    /// a VPN or internal address the browser cannot load from.
    /// </summary>
    public string ExternalUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the URL may point at a loopback, private, or unique local address.
    /// Cloud metadata and link local ranges are always refused regardless.
    /// </summary>
    public bool AllowPrivateNetwork { get; set; } = true;

    /// <summary>Gets or sets the API key or access token, protected at rest.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the username the token was generated for, or empty for a manually entered key.</summary>
    public string AuthenticatedUser { get; set; } = string.Empty;

    /// <summary>Gets or sets the id of that user on the peer, used for user scoped fallbacks.</summary>
    public string AuthenticatedUserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the peer's reported server name.</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>Gets or sets the peer's Jellyfin server id.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what the key can do there, as learned by the last connection test or sign in:
    /// "Administrator", "User", or empty when not yet known. A standard user's key scans what that user
    /// can see and carries that user's history only. Two way history, user sync, and Push or Sync need an
    /// administrator, since Server Sync's own endpoints and the user APIs require elevation.
    /// </summary>
    public string AccessLevel { get; set; } = string.Empty;

    /// <summary>Gets or sets what this server does with the peer.</summary>
    public ServerMode Mode { get; set; } = ServerMode.Pull;

    /// <summary>Gets or sets a value indicating whether the entry takes part in any sync.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Gets or sets the library mappings between this peer and the local server.</summary>
    public List<LibraryMapping> LibraryMappings { get; set; } = new();

    /// <summary>Gets or sets the user mappings between this peer and the local server.</summary>
    public List<UserMapping> UserMappings { get; set; } = new();

    /// <summary>Gets the name shown in logs and the dashboard.</summary>
    [System.Xml.Serialization.XmlIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName
        => !string.IsNullOrWhiteSpace(Name) ? Name
            : !string.IsNullOrWhiteSpace(ServerName) ? ServerName
            : Url;

    /// <summary>Gets a value indicating whether the entry has enough to connect.</summary>
    [System.Xml.Serialization.XmlIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Gets a value indicating whether this server scans the peer on a schedule.</summary>
    [System.Xml.Serialization.XmlIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Pulls => IsEnabled && IsConfigured && Mode is ServerMode.Pull or ServerMode.Sync;

    /// <summary>Gets a value indicating whether this server sends hints to the peer.</summary>
    [System.Xml.Serialization.XmlIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Pushes => IsEnabled && IsConfigured && Mode is ServerMode.Push or ServerMode.Sync;

    /// <summary>Gets the URL the browser should use for images from this peer.</summary>
    [System.Xml.Serialization.XmlIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public string BrowserUrl => !string.IsNullOrEmpty(ExternalUrl) ? ExternalUrl : Url;

    /// <summary>Returns the enabled library mappings of this peer as a fresh list.</summary>
    /// <returns>The enabled mappings.</returns>
    public List<LibraryMapping> GetEnabledLibraryMappings()
        => LibraryMappings?.Where(m => m.IsEnabled).ToList() ?? new List<LibraryMapping>();

    /// <summary>Returns the enabled user mappings of this peer as a fresh list.</summary>
    /// <returns>The enabled mappings.</returns>
    public List<UserMapping> GetEnabledUserMappings()
        => UserMappings?.Where(m => m.IsEnabled).ToList() ?? new List<UserMapping>();

    /// <summary>Makes a new entry key.</summary>
    /// <returns>A 32 character hex key.</returns>
    public static string NewKey() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
}
