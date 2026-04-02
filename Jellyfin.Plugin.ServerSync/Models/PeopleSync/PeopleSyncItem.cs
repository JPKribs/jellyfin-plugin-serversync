using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
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
    /// Gets or sets the source person metadata (JSON blob with all person fields).
    /// </summary>
    public string? SourceMetadataValue { get; set; }

    /// <summary>
    /// Gets or sets the local person metadata (JSON blob with all person fields).
    /// </summary>
    public string? LocalMetadataValue { get; set; }

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
    /// Gets a value indicating whether metadata has changes.
    /// </summary>
    public bool HasMetadataChanges
    {
        get
        {
            if (string.IsNullOrEmpty(SourceMetadataValue))
            {
                return false;
            }

            return !JsonComparisonUtility.JsonEquals(SourceMetadataValue, LocalMetadataValue);
        }
    }

    /// <summary>
    /// Gets a value indicating whether images have changes.
    /// Compares source images against local images by type count and file size.
    /// </summary>
    public bool HasImagesChanges
    {
        get
        {
            if (string.IsNullOrEmpty(SourceImagesValue))
            {
                return false;
            }

            // No local images but source has images — needs sync
            if (string.IsNullOrEmpty(LocalImagesValue))
            {
                return true;
            }

            return !ImagesMatch(SourceImagesValue, LocalImagesValue);
        }
    }

    /// <summary>
    /// Compares source and local image collections by type, count, and file size.
    /// </summary>
    private static bool ImagesMatch(string sourceJson, string localJson)
    {
        try
        {
            var source = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(sourceJson);
            var local = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(localJson);

            if (source == null || local == null)
            {
                return source == null && local == null;
            }

            // Check that local has every image type that source has
            foreach (var kvp in source)
            {
                if (!local.TryGetValue(kvp.Key, out var localImages))
                {
                    return false; // Missing image type locally
                }

                if (kvp.Value.Count != localImages.Count)
                {
                    return false; // Different number of images for this type
                }

                // Compare by size — if local file size doesn't match source, needs re-sync
                for (int i = 0; i < kvp.Value.Count; i++)
                {
                    if (kvp.Value[i].Size > 0 && localImages[i].Size > 0
                        && kvp.Value[i].Size != localImages[i].Size)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        catch
        {
            // If we can't parse, assume changes exist
            return false;
        }
    }

    /// <summary>
    /// Gets a value indicating whether there are any changes to sync.
    /// </summary>
    public bool HasChanges => HasMetadataChanges || HasImagesChanges;
}
