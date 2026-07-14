using System;

namespace Jellyfin.Plugin.ServerSync.Models.UserSync;

/// <summary>
/// Aggregated DTO representing a user mapping with all property categories combined.
/// Used for the consolidated user sync table view (1 row per user instead of 3).
/// </summary>
public class UserSyncUserDto
{
    // ===== User Identification =====

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

    // ===== Record IDs (for bulk operations) =====

    /// <summary>
    /// Gets or sets the database ID of the Policy sync item.
    /// </summary>
    public long? PolicyId { get; set; }

    /// <summary>
    /// Gets or sets the database ID of the Configuration sync item.
    /// </summary>
    public long? ConfigurationId { get; set; }

    /// <summary>
    /// Gets or sets the database ID of the ProfileImage sync item.
    /// </summary>
    public long? ProfileImageId { get; set; }

    // ===== Individual Category Statuses =====

    /// <summary>
    /// Gets or sets the Policy sync status.
    /// </summary>
    public string? PolicyStatus { get; set; }

    /// <summary>
    /// Gets or sets the Configuration sync status.
    /// </summary>
    public string? ConfigurationStatus { get; set; }

    /// <summary>
    /// Gets or sets the ProfileImage sync status.
    /// </summary>
    public string? ProfileImageStatus { get; set; }

    // ===== Individual Category Change Detection =====

    /// <summary>
    /// Gets or sets whether the Policy has changes.
    /// </summary>
    public bool PolicyHasChanges { get; set; }

    /// <summary>
    /// Gets or sets whether the Configuration has changes.
    /// </summary>
    public bool ConfigurationHasChanges { get; set; }

    /// <summary>
    /// Gets or sets whether the ProfileImage has changes.
    /// </summary>
    public bool ProfileImageHasChanges { get; set; }

    /// <summary>
    /// Gets or sets the Policy changes summary.
    /// </summary>
    public string? PolicyChangesSummary { get; set; }

    /// <summary>
    /// Gets or sets the Configuration changes summary.
    /// </summary>
    public string? ConfigurationChangesSummary { get; set; }

    /// <summary>
    /// Gets or sets the ProfileImage changes summary.
    /// </summary>
    public string? ProfileImageChangesSummary { get; set; }

    // ===== Source Server =====

    /// <summary>
    /// Gets or sets the source server URL (for image display).
    /// </summary>
    public string? SourceServerUrl { get; set; }

    // ===== Computed Aggregate Fields =====

    /// <summary>
    /// Gets or sets the overall status (Errored > Queued > Ignored > Synced).
    /// </summary>
    public string OverallStatus { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the total changes display (e.g., "1 policy, 2 config" or "No Changes").
    /// </summary>
    public string TotalChanges { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether any category has changes.
    /// </summary>
    public bool HasChanges { get; set; }

    /// <summary>
    /// Gets or sets the most recent LastSyncTime across all categories.
    /// </summary>
    public DateTime? LastSyncTime { get; set; }

    /// <summary>
    /// Gets or sets the combined error messages if any.
    /// </summary>
    public string? ErrorMessage { get; set; }
}
