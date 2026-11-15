using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using JPKribs.Jellyfin.Base;

namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// One server entry. Its place in <see cref="Jellyfin.Plugin.ServerSync.Configuration.PluginConfiguration.Servers"/> is its Rank, so an earlier entry wins when two offer the same item.
/// </summary>
public class SourceServer
{
    /// <summary>The <see cref="AccessLevel"/> of a key that belongs to an administrator.</summary>
    public const string AdministratorAccess = "Administrator";

    /// <summary>The <see cref="AccessLevel"/> of a key that belongs to a standard user.</summary>
    public const string StandardUserAccess = "User";

    /// <summary>
    /// Gets or sets the stable key of this entry, carried on every sync table row that came from it so a reorder or rename never orphans rows.
    /// </summary>
    public string Key { get; set; } = NewKey();

    /// <summary>Gets or sets the name an administrator gave the entry. It falls back to the server's reported name, then its Server URL.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the Server URL this server connects to.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the External URL a browser loads images from, for a server reached over an address the browser cannot use.
    /// </summary>
    public string ExternalUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the Server URL may be a loopback, private, or unique local address.
    /// Cloud metadata and link local ranges are always refused.
    /// </summary>
    public bool AllowPrivateNetwork { get; set; } = true;

    /// <summary>Gets or sets the API Key or access token, protected at rest.</summary>
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
    /// Gets or sets what the key can do there, "Administrator", "User", or empty when not yet known.
    /// A standard user's key scans what that user sees and carries only that user's History, and Push, Sync, and Users need an administrator.
    /// </summary>
    public string AccessLevel { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the key's user may download files there, or null when not yet known.</summary>
    public bool? CanDownload { get; set; }

    /// <summary>Gets or sets the Direction changes travel between this server and that one.</summary>
    public ServerMode Mode { get; set; } = ServerMode.Pull;

    /// <summary>Gets or sets a value indicating whether the entry takes part in any sync.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether that server may stream this server's files for its links, which is off until an administrator allows it.
    /// </summary>
    public bool AllowLinking { get; set; }

    /// <summary>Gets or sets the most link streams that server may hold at once, or 0 for no limit.</summary>
    public int LinkMaxStreams { get; set; }

    /// <summary>
    /// Gets or sets the most link streams this server takes from that server at once, or 0 for no limit.
    /// </summary>
    public int LinkTakeMaxStreams { get; set; }

    /// <summary>
    /// Gets or sets a per user stream limit for the whole entry.
    /// <see cref="Jellyfin.Plugin.ServerSync.Configuration.PluginConfiguration.SanitizeValues"/> moves it onto every user mapping that sets none.
    /// </summary>
    public int LinkMaxStreamsPerUser { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a user's own playback on this server counts toward the
    /// limit their user mapping sets.
    /// </summary>
    public bool LinkCountOwnSessions { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a file above a user's remote bitrate limit is refused,
    /// since a link streams the file as it is.
    /// </summary>
    public bool LinkHonorBitrateLimit { get; set; }

    /// <summary>
    /// Gets or sets the most each link stream to that server is sent at, in megabits per second, or 0
    /// for no limit.
    /// </summary>
    public int LinkStreamSpeedMbps { get; set; }

    /// <summary>
    /// Gets or sets the most each link stream is sent at during the bandwidth schedule's hours, in megabits per second, or 0 for no limit.
    /// </summary>
    public int LinkScheduledStreamSpeedMbps { get; set; }

    /// <summary>Reads the most each link stream to that server is sent at, in bytes per second, or 0 for no limit.</summary>
    /// <param name="scheduled">Whether the bandwidth schedule's hours apply at this moment.</param>
    /// <returns>The rate in bytes per second.</returns>
    public long GetLinkStreamSpeedBytes(bool scheduled)
        => RateUtilities.MbpsToBytesPerSecond(scheduled ? LinkScheduledStreamSpeedMbps : LinkStreamSpeedMbps);

    /// <summary>
    /// Reads the most a link stream or cache download for one user is sent at, the lower of this entry's speed and that user mapping's speed.
    /// </summary>
    /// <param name="scheduled">Whether the bandwidth schedule's hours apply at this moment.</param>
    /// <param name="localUserId">The user here the stream runs as, or null for none.</param>
    /// <returns>The rate in bytes per second, or 0 for no limit.</returns>
    public long GetLinkStreamSpeedBytes(bool scheduled, Guid? localUserId)
    {
        var server = GetLinkStreamSpeedBytes(scheduled);
        var user = RateUtilities.MbpsToBytesPerSecond(MappingForLocalUser(localUserId)?.LinkStreamSpeedMbps ?? 0);
        return server > 0 && user > 0 ? Math.Min(server, user) : Math.Max(server, user);
    }

    /// <summary>
    /// Reads the most link streams one user here may hold at once, from that user's mapping and never above the entry's total.
    /// </summary>
    /// <param name="localUserId">The user here the stream runs as.</param>
    /// <returns>The limit, or 0 for no limit.</returns>
    public int GetLinkMaxStreamsFor(Guid? localUserId)
    {
        var perUser = Math.Max(0, MappingForLocalUser(localUserId)?.LinkMaxStreams ?? 0);
        var total = Math.Max(0, LinkMaxStreams);
        return total > 0 && (perUser == 0 || perUser > total) ? total : perUser;
    }

    /// <summary>Finds the enabled user mapping for a user on this server.</summary>
    private UserMapping? MappingForLocalUser(Guid? localUserId)
        => localUserId is { } user
            ? UserMappings?.FirstOrDefault(m => m.IsEnabled && Guid.TryParse(m.LocalUserId, out var id) && id == user)
            : null;

    /// <summary>
    /// Gets or sets this server's user that streams for unmapped users of that server. Empty refuses them.
    /// </summary>
    public string LinkFallbackUserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the library mappings between that server and this one.</summary>
    public List<LibraryMapping> LibraryMappings { get; set; } = new();

    /// <summary>Gets or sets the user mappings between that server and this one.</summary>
    public List<UserMapping> UserMappings { get; set; } = new();

    /// <summary>Gets the name shown in logs and the dashboard.</summary>
    [XmlIgnore]
    [JsonIgnore]
    public string DisplayName
        => !string.IsNullOrWhiteSpace(Name) ? Name
            : !string.IsNullOrWhiteSpace(ServerName) ? ServerName
            : Url;

    /// <summary>Gets a value indicating whether the entry has enough to connect.</summary>
    [XmlIgnore]
    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Gets a value indicating whether this server scans the peer on a schedule.</summary>
    [XmlIgnore]
    [JsonIgnore]
    public bool Pulls => IsEnabled && IsConfigured && Mode is ServerMode.Pull or ServerMode.Sync;

    /// <summary>Gets a value indicating whether this server sends hints to the peer.</summary>
    [XmlIgnore]
    [JsonIgnore]
    public bool Pushes => IsEnabled && IsConfigured && Mode is ServerMode.Push or ServerMode.Sync;

    /// <summary>
    /// Gets a value indicating whether the peer may link to this server's files, which takes Allow Linking and a Push or Sync direction.
    /// </summary>
    [XmlIgnore]
    [JsonIgnore]
    public bool OffersLinks => IsEnabled && AllowLinking && Mode is ServerMode.Push or ServerMode.Sync;

    /// <summary>Gets the URL the browser should use for images from this peer.</summary>
    [XmlIgnore]
    [JsonIgnore]
    public string BrowserUrl => !string.IsNullOrEmpty(ExternalUrl) ? ExternalUrl : Url;

    /// <summary>Returns the enabled library mappings of this peer as a fresh list.</summary>
    /// <returns>The enabled mappings.</returns>
    public List<LibraryMapping> GetEnabledLibraryMappings()
        => LibraryMappings?.Where(m => m.IsEnabled).ToList() ?? new List<LibraryMapping>();

    /// <summary>Gets a value indicating whether the key belongs to a standard user.</summary>
    [XmlIgnore]
    [JsonIgnore]
    public bool IsStandardUser => string.Equals(AccessLevel, StandardUserAccess, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the enabled user mappings of this peer as a fresh list.</summary>
    /// <returns>The enabled mappings.</returns>
    public List<UserMapping> GetEnabledUserMappings()
        => UserMappings?.Where(m => m.IsEnabled && MaySyncUser(m.SourceUserId)).ToList() ?? new List<UserMapping>();

    /// <summary>Whether a source user's settings and history may be synced, which a standard user's key limits to that user.</summary>
    /// <param name="sourceUserId">The user's id on the source.</param>
    /// <returns><c>true</c> when the user may be synced.</returns>
    public bool MaySyncUser(string? sourceUserId)
    {
        if (!IsStandardUser)
        {
            return true;
        }

        if (!Guid.TryParse(AuthenticatedUserId, out var self))
        {
            return true;
        }

        return Guid.TryParse(sourceUserId, out var source) && source == self;
    }

    /// <summary>Makes a new entry key.</summary>
    /// <returns>A 32 character hex key.</returns>
    public static string NewKey() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
}
