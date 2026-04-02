using System;

namespace Jellyfin.Plugin.ServerSync.Models.PeopleSync;

/// <summary>
/// DTO for people sync items returned by the API.
/// </summary>
public class PeopleSyncItemDto
{
    /// <summary>
    /// Gets or sets the database ID.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the person name (natural key).
    /// </summary>
    public string PersonName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source server person ID.
    /// </summary>
    public string? SourcePersonId { get; set; }

    /// <summary>
    /// Gets or sets the local server person ID.
    /// </summary>
    public string? LocalPersonId { get; set; }

    /// <summary>
    /// Gets or sets the source metadata (JSON blob).
    /// </summary>
    public string? SourceMetadataValue { get; set; }

    /// <summary>
    /// Gets or sets the local metadata (JSON blob).
    /// </summary>
    public string? LocalMetadataValue { get; set; }

    /// <summary>
    /// Gets or sets the source images value (JSON).
    /// </summary>
    public string? SourceImagesValue { get; set; }

    /// <summary>
    /// Gets or sets the local images value (JSON).
    /// </summary>
    public string? LocalImagesValue { get; set; }

    /// <summary>
    /// Gets or sets whether metadata has changes.
    /// </summary>
    public bool HasMetadataChanges { get; set; }

    /// <summary>
    /// Gets or sets whether images have changes.
    /// </summary>
    public bool HasImagesChanges { get; set; }

    /// <summary>
    /// Gets or sets whether any changes exist.
    /// </summary>
    public bool HasChanges { get; set; }

    /// <summary>
    /// Gets or sets the sync status.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the status was last updated.
    /// </summary>
    public DateTime StatusDate { get; set; }

    /// <summary>
    /// Gets or sets the last successful sync time.
    /// </summary>
    public DateTime? LastSyncTime { get; set; }

    /// <summary>
    /// Gets or sets the error message if status is Errored.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the source server URL for image rendering.
    /// </summary>
    public string? SourceServerUrl { get; set; }

    /// <summary>
    /// Gets or sets the source server API key for image rendering.
    /// </summary>
    public string? SourceServerApiKey { get; set; }
}
