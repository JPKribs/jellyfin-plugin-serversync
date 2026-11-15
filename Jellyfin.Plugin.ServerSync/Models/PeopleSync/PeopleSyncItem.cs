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
    /// Gets or sets the person's item ID on the source.
    /// </summary>
    public string? SourcePersonId { get; set; }

    /// <summary>
    /// Gets or sets the person's item ID on this server. A scan writes no row for a person with no local match.
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

    /// <summary>
    /// Gets a value indicating whether the metadata blob has changes worth syncing.
    /// </summary>
    public bool HasMetadataChanges => !string.IsNullOrEmpty(LocalPersonId) && Metadata.HasChanges;

    /// <summary>
    /// Gets a value indicating whether the image manifest has changes worth syncing.
    /// </summary>
    public bool HasImagesChanges => !string.IsNullOrEmpty(LocalPersonId) && Images.HasChanges;

    /// <summary>
    /// Gets a value indicating whether the metadata or images differ. Both parts also need a local match, so a stale row is never queued.
    /// </summary>
    public override bool HasChanges => HasMetadataChanges || HasImagesChanges;

    /// <inheritdoc />
    public override void MarkSynced()
    {
        Metadata.MarkSynced();
        Images.MarkSynced();
    }

    /// <summary>
    /// Reads the source's image manifest from <see cref="Images"/>.
    /// </summary>
    /// <param name="manifest">The parsed manifest, set when the method returns <c>true</c>.</param>
    /// <returns><c>true</c> when the row holds a manifest that could be read, and <c>false</c> when it is missing or malformed.</returns>
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
