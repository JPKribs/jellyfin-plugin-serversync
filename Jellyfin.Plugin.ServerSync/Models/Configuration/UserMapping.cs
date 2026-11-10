namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Represents a mapping between a source server user and a local user.
/// Used for syncing watch history and user data between servers.
/// </summary>
public class UserMapping
{
    /// <summary>
    /// The user ID on the source server.
    /// </summary>
    public string SourceUserId { get; set; } = string.Empty;

    /// <summary>
    /// The username on the source server (for display purposes).
    /// </summary>
    public string SourceUserName { get; set; } = string.Empty;

    /// <summary>
    /// The user ID on the local server.
    /// </summary>
    public string LocalUserId { get; set; } = string.Empty;

    /// <summary>
    /// The username on the local server (for display purposes).
    /// </summary>
    public string LocalUserName { get; set; } = string.Empty;

    /// <summary>
    /// Whether this mapping is currently enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the most link streams this user may hold at once on this server, or null to use
    /// the server entry's own per user limit. 0 means no limit for this user.
    /// </summary>
    public int? LinkMaxStreams { get; set; }

    /// <summary>
    /// Gets or sets the most each stream or cache download for this user is sent at, in megabits per
    /// second, or null to use the server entry's speed. The lower of the two applies when both are set.
    /// </summary>
    public int? LinkStreamSpeedMbps { get; set; }
}
