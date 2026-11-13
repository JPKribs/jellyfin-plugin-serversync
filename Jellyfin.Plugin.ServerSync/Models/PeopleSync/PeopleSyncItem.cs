using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Common.Comparators;

namespace Jellyfin.Plugin.ServerSync.Models.PeopleSync;

/// <summary>
/// Sync record for a Person entity, matched across servers by name. Two
/// comparable fields: <see cref="Metadata"/> (JSON blob of person fields) and
/// <see cref="Images"/> (JSON manifest of available image types/sizes).
/// </summary>
public class PeopleSyncItem : SyncRecord
{
    /// <summary>
    /// Gets or sets the person name, natural key for cross-server matching.
    /// </summary>
    public string PersonName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source server's person item ID.
    /// </summary>
    public string? SourcePersonId { get; set; }

    /// <summary>
    /// Gets or sets the local server's person item ID. Null when no local
    /// match exists. The refresh skips writing rows in that case.
    /// </summary>
    public string? LocalPersonId { get; set; }

    /// <summary>
    /// Gets the metadata blob (Source/Local snapshots, hashes).
    /// </summary>
    public SyncableValue<string> Metadata { get; } = new() { Comparator = new JsonBlobComparator() };

    /// <summary>
    /// Gets the image manifest (Source/Local snapshots, hashes).
    /// </summary>
    public SyncableValue<string> Images { get; } = new() { Comparator = new ImageManifestComparator() };

    // ===== Computed change detection =====

    /// <summary>
    /// Gets a value indicating whether the metadata blob has changes worth syncing.
    /// </summary>
    public bool HasMetadataChanges => !string.IsNullOrEmpty(LocalPersonId) && Metadata.HasChanges;

    /// <summary>
    /// Gets a value indicating whether the image manifest has changes worth syncing.
    /// </summary>
    public bool HasImagesChanges => !string.IsNullOrEmpty(LocalPersonId) && Images.HasChanges;

    /// <inheritdoc />
    /// <remarks>
    /// No row should exist without a local match (Refresh skips them), but both
    /// parts check for one anyway, so a stale row is never queued.
    /// </remarks>
    public override bool HasChanges => HasMetadataChanges || HasImagesChanges;

    /// <inheritdoc />
    public override void MarkSynced()
    {
        Metadata.MarkSynced();
        Images.MarkSynced();
    }

    // ===== Image manifest helper =====
    // Used by apply paths that need to look up specific image entries.

    /// <summary>
    /// Tries to deserialize <see cref="Images"/>.<see cref="SyncableValue{T}.Source"/>
    /// as an image manifest. Returns false if missing or malformed.
    /// </summary>
    public bool TryGetSourceImageManifest(out Dictionary<string, List<ImageInfoDto>>? manifest)
    {
        manifest = null;
        if (string.IsNullOrEmpty(Images.Source))
        {
            return false;
        }

        try
        {
            manifest = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(Images.Source);
            return manifest != null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
