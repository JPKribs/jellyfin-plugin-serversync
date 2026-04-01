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
    /// Gets or sets the source overview/biography.
    /// </summary>
    public string? SourceOverview { get; set; }

    /// <summary>
    /// Gets or sets the local overview/biography.
    /// </summary>
    public string? LocalOverview { get; set; }

    /// <summary>
    /// Gets or sets the source provider IDs (JSON).
    /// </summary>
    public string? SourceProviderIds { get; set; }

    /// <summary>
    /// Gets or sets the local provider IDs (JSON).
    /// </summary>
    public string? LocalProviderIds { get; set; }

    /// <summary>
    /// Gets or sets whether the overview has changes.
    /// </summary>
    public bool HasOverviewChanges { get; set; }

    /// <summary>
    /// Gets or sets whether provider IDs have changes.
    /// </summary>
    public bool HasProviderIdChanges { get; set; }

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
}
