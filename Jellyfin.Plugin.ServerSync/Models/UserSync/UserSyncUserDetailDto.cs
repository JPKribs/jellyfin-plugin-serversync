using System;

namespace Jellyfin.Plugin.ServerSync.Models.UserSync;

/// <summary>
/// Detailed DTO for user sync modal showing all property categories.
/// Contains full UserSyncItemDto for each category.
/// </summary>
public class UserSyncUserDetailDto
{
    /// <summary>
    /// Gets or sets the source server user ID.
    /// </summary>
    public string SourceUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local server user ID.
    /// </summary>
    public string LocalUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source user's display name.
    /// </summary>
    public string? SourceUserName { get; set; }

    /// <summary>
    /// Gets or sets the local user's display name.
    /// </summary>
    public string? LocalUserName { get; set; }

    /// <summary>
    /// Gets or sets the overall status (Errored > Queued > Ignored > Synced).
    /// </summary>
    public string OverallStatus { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the most recent LastSyncTime across all categories.
    /// </summary>
    public DateTime? LastSyncTime { get; set; }

    /// <summary>
    /// Gets or sets the combined error messages if any.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the Policy sync item details (null if not available).
    /// </summary>
    public UserSyncItemDto? PolicyItem { get; set; }

    /// <summary>
    /// Gets or sets the Configuration sync item details (null if not available).
    /// </summary>
    public UserSyncItemDto? ConfigurationItem { get; set; }

    /// <summary>
    /// Gets or sets the ProfileImage sync item details (null if not available).
    /// </summary>
    public UserSyncItemDto? ProfileImageItem { get; set; }

    /// <summary>
    /// Gets or sets whether Policy sync is enabled in configuration.
    /// </summary>
    public bool PolicyEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether Configuration sync is enabled in configuration.
    /// </summary>
    public bool ConfigurationEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether ProfileImage sync is enabled in configuration.
    /// </summary>
    public bool ProfileImageEnabled { get; set; }
}
