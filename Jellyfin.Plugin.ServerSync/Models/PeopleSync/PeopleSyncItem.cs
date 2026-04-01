using System;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Utilities;

namespace Jellyfin.Plugin.ServerSync.Models.PeopleSync;

/// <summary>
/// Represents a sync record for a Person entity.
/// Matched across servers by name (unique).
/// </summary>
public class PeopleSyncItem
{
    /// <summary>
    /// Gets or sets the unique database identifier.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the person name (natural key for matching across servers).
    /// </summary>
    public string PersonName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source server person item ID.
    /// </summary>
    public string? SourcePersonId { get; set; }

    /// <summary>
    /// Gets or sets the local server person item ID.
    /// </summary>
    public string? LocalPersonId { get; set; }

    // ===== Metadata =====

    /// <summary>
    /// Gets or sets the source person overview/biography.
    /// </summary>
    public string? SourceOverview { get; set; }

    /// <summary>
    /// Gets or sets the local person overview/biography.
    /// </summary>
    public string? LocalOverview { get; set; }

    /// <summary>
    /// Gets or sets the source provider IDs (JSON dictionary, e.g. TMDB, IMDB person IDs).
    /// </summary>
    public string? SourceProviderIds { get; set; }

    /// <summary>
    /// Gets or sets the local provider IDs (JSON dictionary).
    /// </summary>
    public string? LocalProviderIds { get; set; }

    // ===== Images =====

    /// <summary>
    /// Gets or sets the source images value (JSON).
    /// </summary>
    public string? SourceImagesValue { get; set; }

    /// <summary>
    /// Gets or sets the local images value (JSON).
    /// </summary>
    public string? LocalImagesValue { get; set; }

    /// <summary>
    /// Gets or sets the source images hash (for change detection).
    /// </summary>
    public string? SourceImagesHash { get; set; }

    /// <summary>
    /// Gets or sets the last synced images hash.
    /// </summary>
    public string? SyncedImagesHash { get; set; }

    // ===== Sync Tracking =====

    /// <summary>
    /// Gets or sets the sync status.
    /// </summary>
    public BaseSyncStatus Status { get; set; }

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

    // ===== Computed Properties =====

    /// <summary>
    /// Gets a value indicating whether the overview/bio has changes.
    /// </summary>
    public bool HasOverviewChanges =>
        !string.IsNullOrEmpty(SourceOverview)
        && !string.Equals(SourceOverview, LocalOverview, StringComparison.Ordinal);

    /// <summary>
    /// Gets a value indicating whether provider IDs have changes.
    /// </summary>
    public bool HasProviderIdChanges =>
        !string.IsNullOrEmpty(SourceProviderIds)
        && !JsonComparisonUtility.JsonEquals(SourceProviderIds, LocalProviderIds);

    /// <summary>
    /// Gets a value indicating whether images have changes.
    /// </summary>
    public bool HasImagesChanges =>
        !string.IsNullOrEmpty(SourceImagesValue)
        && (string.IsNullOrEmpty(LocalImagesValue)
            || !JsonComparisonUtility.JsonEquals(SourceImagesValue, LocalImagesValue));

    /// <summary>
    /// Gets a value indicating whether there are any changes to sync.
    /// </summary>
    public bool HasChanges => HasOverviewChanges || HasProviderIdChanges || HasImagesChanges;
}
