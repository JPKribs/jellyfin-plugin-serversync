using System;

namespace Jellyfin.Plugin.ServerSync.Models.MetadataSync;

/// <summary>
/// Metadata sync item information for API responses.
/// One record per item containing all categories.
/// </summary>
public class MetadataSyncItemDto
{
    /// <summary>
    /// Gets or sets the database ID.
    /// </summary>
    public long Id { get; set; }

    // ===== Item Identification =====

    /// <summary>
    /// Gets or sets the source library ID.
    /// </summary>
    public string SourceLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local library ID.
    /// </summary>
    public string LocalLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source library name (for display).
    /// </summary>
    public string? SourceLibraryName { get; set; }

    /// <summary>
    /// Gets or sets the local library name (for display).
    /// </summary>
    public string? LocalLibraryName { get; set; }

    /// <summary>
    /// Gets or sets the source item ID.
    /// </summary>
    public string SourceItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local item ID (if matched).
    /// </summary>
    public string? LocalItemId { get; set; }

    /// <summary>
    /// Gets or sets the item name for display.
    /// </summary>
    public string? ItemName { get; set; }

    /// <summary>
    /// Gets or sets the source file path.
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Gets or sets the local file path.
    /// </summary>
    public string? LocalPath { get; set; }

    // ===== Category Values (for modal display) =====

    /// <summary>
    /// Gets or sets the source metadata value (JSON).
    /// </summary>
    public string? SourceMetadataValue { get; set; }

    /// <summary>
    /// Gets or sets the local metadata value (JSON).
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
    /// Gets or sets the source people value (JSON).
    /// </summary>
    public string? SourcePeopleValue { get; set; }

    /// <summary>
    /// Gets or sets the local people value (JSON).
    /// </summary>
    public string? LocalPeopleValue { get; set; }

    /// <summary>
    /// Gets or sets the source studios value (JSON array of studio names).
    /// </summary>
    public string? SourceStudiosValue { get; set; }

    /// <summary>
    /// Gets or sets the local studios value (JSON array of studio names).
    /// </summary>
    public string? LocalStudiosValue { get; set; }

    // ===== Change Detection =====

    /// <summary>
    /// Gets or sets whether metadata has changes.
    /// </summary>
    public bool HasMetadataChanges { get; set; }

    /// <summary>
    /// Gets or sets whether images have changes.
    /// </summary>
    public bool HasImagesChanges { get; set; }

    /// <summary>
    /// Gets or sets whether people have changes.
    /// </summary>
    public bool HasPeopleChanges { get; set; }

    /// <summary>
    /// Gets or sets whether studios have changes.
    /// </summary>
    public bool HasStudiosChanges { get; set; }

    /// <summary>
    /// Gets or sets whether there are any changes to sync.
    /// </summary>
    public bool HasChanges { get; set; }

    /// <summary>
    /// Gets or sets a summary of the changes.
    /// </summary>
    public string? ChangesSummary { get; set; }

    /// <summary>
    /// Gets or sets the comparator's precise reason the image manifests
    /// differ (e.g. which type and index, and both sizes). Null when images
    /// match or on list views. The modal shows per-type size SUMS, which can
    /// look identical while per-index sizes differ — without this the
    /// "Images: Changes" badge is unexplainable from the UI.
    /// </summary>
    public string? ImagesChangesDetail { get; set; }

    /// <summary>
    /// Gets or sets the names of the metadata fields that differ, computed
    /// server-side over the full blobs. The modal renders a fixed field
    /// list, so a field added to the sync blob but not to the modal would
    /// otherwise show a "Changes" badge with no visible difference.
    /// </summary>
    public string? MetadataChangesDetail { get; set; }

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
