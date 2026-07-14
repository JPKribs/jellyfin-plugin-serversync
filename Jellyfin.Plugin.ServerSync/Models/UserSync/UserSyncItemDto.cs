using System;

namespace Jellyfin.Plugin.ServerSync.Models.UserSync;

/// <summary>
/// User sync item information for API responses.
/// Similar to HistorySyncItemDto in structure.
/// </summary>
public class UserSyncItemDto
{
    /// <summary>
    /// Gets or sets the database ID.
    /// </summary>
    public long Id { get; set; }

    // ===== User Mapping =====

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

    // ===== Property Info =====

    /// <summary>
    /// Gets or sets the property category (Policy, Configuration, ProfileImage).
    /// </summary>
    public string PropertyCategory { get; set; } = string.Empty;

    // ===== Values =====

    /// <summary>
    /// Gets or sets the source value.
    /// </summary>
    public string? SourceValue { get; set; }

    /// <summary>
    /// Gets or sets the local value.
    /// </summary>
    public string? LocalValue { get; set; }

    /// <summary>
    /// Gets or sets the merged value (what will be applied).
    /// </summary>
    public string? MergedValue { get; set; }

    // ===== Image Size (for ProfileImage category) =====

    /// <summary>
    /// Gets or sets the source image size in bytes.
    /// </summary>
    public long? SourceImageSize { get; set; }

    /// <summary>
    /// Gets or sets the local image size in bytes.
    /// </summary>
    public long? LocalImageSize { get; set; }

    /// <summary>
    /// Gets or sets the source image size formatted (e.g., "1.2 MB").
    /// </summary>
    public string? SourceImageSizeFormatted { get; set; }

    /// <summary>
    /// Gets or sets the local image size formatted (e.g., "1.1 MB").
    /// </summary>
    public string? LocalImageSizeFormatted { get; set; }

    // ===== Change Detection =====

    /// <summary>
    /// Gets or sets whether there are changes to sync.
    /// </summary>
    public bool HasChanges { get; set; }

    /// <summary>
    /// Gets or sets a summary of the changes.
    /// </summary>
    public string? ChangesSummary { get; set; }

    // ===== Source Server =====

    /// <summary>
    /// Gets or sets the source server URL (for image display).
    /// </summary>
    public string? SourceServerUrl { get; set; }

    // ===== Sync Status =====

    /// <summary>
    /// Gets or sets the sync status.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the status was last changed.
    /// </summary>
    public DateTime StatusDate { get; set; }

    /// <summary>
    /// Gets or sets when the item was last synced.
    /// </summary>
    public DateTime? LastSyncTime { get; set; }

    /// <summary>
    /// Gets or sets the error message if status is Errored.
    /// </summary>
    public string? ErrorMessage { get; set; }
}
