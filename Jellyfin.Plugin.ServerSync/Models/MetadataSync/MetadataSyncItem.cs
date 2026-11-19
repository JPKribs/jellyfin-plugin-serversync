using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Common.Comparators;

namespace Jellyfin.Plugin.ServerSync.Models.MetadataSync;

/// <summary>
/// One row per source library and source item, holding the metadata, images, people, studios, and media segments as separate JSON values.
/// Each value keeps its own source, local, and synced copies with the hashes of what was last applied.
/// </summary>
public class MetadataSyncItem : SyncRecord
{
    /// <summary>
    /// Gets or sets the source library ID, first component of the natural key.
    /// </summary>
    public string SourceLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local library ID.
    /// </summary>
    public string LocalLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source server item ID, second component of the
    /// natural key.
    /// </summary>
    public string SourceItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local server item ID (resolved by path lookup).
    /// </summary>
    public string? LocalItemId { get; set; }

    /// <summary>
    /// Gets or sets the item display name.
    /// </summary>
    public string? ItemName { get; set; }

    /// <summary>
    /// Gets or sets the source file path (used for path-based matching).
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Gets or sets the local file path (translated from source via library mapping).
    /// </summary>
    public string? LocalPath { get; set; }

    /// <summary>
    /// Gets or sets the item type (Movie, Series, Season, Episode, etc.).
    /// </summary>
    public string? ItemType { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this is a folder-type item
    /// (Series, Season, MusicAlbum, MusicArtist, BoxSet).
    /// </summary>
    public bool IsFolder { get; set; }

    /// <summary>
    /// Gets the metadata blob, such as the name, overview, ratings, dates, genres, tags, and provider IDs.
    /// </summary>
    public SyncableValue<string> Metadata { get; } = new() { Comparator = new JsonBlobComparator() };

    /// <summary>
    /// Gets the image manifest, compared by the count and file sizes of each image type.
    /// </summary>
    public SyncableValue<string> Images { get; } = new() { Comparator = new ImageManifestComparator() };

    /// <summary>
    /// Gets the people array, the cast and crew by name, role, and type in billing order.
    /// </summary>
    public SyncableValue<string> People { get; } = new() { Comparator = new PeopleListComparator() };

    /// <summary>
    /// Gets the studio names.
    /// </summary>
    public SyncableValue<string> Studios { get; } = new() { Comparator = new JsonBlobComparator() };

    /// <summary>
    /// Gets the media segments, as a <see cref="MediaSegmentsValue"/>, where the local value is what the local segment table holds.
    /// </summary>
    public SyncableValue<string> Segments { get; } = new() { Comparator = new JsonBlobComparator() };

    /// <summary>
    /// Gets a value indicating whether the metadata blob has changes to sync, which takes a local item to apply them to.
    /// </summary>
    public bool HasMetadataChanges
        => !string.IsNullOrEmpty(LocalItemId) && !string.IsNullOrEmpty(Metadata.Source) && Metadata.HasChanges;

    /// <summary>
    /// Gets a value indicating whether the image manifest has changes worth
    /// syncing.
    /// </summary>
    public bool HasImagesChanges
        => !string.IsNullOrEmpty(LocalItemId) && !string.IsNullOrEmpty(Images.Source) && Images.HasChanges;

    /// <summary>
    /// Gets a value indicating whether the people array has changes worth
    /// syncing.
    /// </summary>
    public bool HasPeopleChanges
        => !string.IsNullOrEmpty(LocalItemId) && !string.IsNullOrEmpty(People.Source) && People.HasChanges;

    /// <summary>
    /// Gets a value indicating whether the studios array has changes worth
    /// syncing. An empty <c>"[]"</c> source is treated as "nothing to sync"
    /// to avoid wiping out local studio assignments when the source has none.
    /// </summary>
    public bool HasStudiosChanges
        => !string.IsNullOrEmpty(LocalItemId) && !string.IsNullOrEmpty(Studios.Source) && Studios.Source != "[]" && Studios.HasChanges;

    /// <summary>
    /// Gets a value indicating whether the media segments have changes to sync, which takes a local item to apply them to.
    /// </summary>
    public bool HasSegmentsChanges
        => !string.IsNullOrEmpty(LocalItemId) && !string.IsNullOrEmpty(Segments.Source) && Segments.HasChanges;

    /// <inheritdoc />
    public override bool HasChanges => HasMetadataChanges || HasImagesChanges || HasPeopleChanges || HasStudiosChanges || HasSegmentsChanges;

    /// <inheritdoc />
    public override void MarkSynced()
    {
        Metadata.MarkSynced();
        Images.MarkSynced();
        People.MarkSynced();
        Studios.MarkSynced();
        Segments.MarkSynced();
    }

    /// <summary>
    /// Gets a display-friendly summary of which categories have changes.
    /// </summary>
    public string ChangesSummary
    {
        get
        {
            if (string.IsNullOrEmpty(LocalItemId))
            {
                return "Local item not found";
            }

            if (!HasChanges)
            {
                return "No changes";
            }

            var changes = new List<string>();

            if (HasMetadataChanges)
            {
                var diffCount = JsonComparisonUtility.CountDifferences(Metadata.Source, Metadata.Local);
                changes.Add(diffCount == 1 ? "1 metadata field" : $"{diffCount} metadata fields");
            }

            if (HasImagesChanges)
            {
                changes.Add("Images");
            }

            if (HasPeopleChanges)
            {
                changes.Add("People");
            }

            if (HasStudiosChanges)
            {
                changes.Add("Studios");
            }

            if (HasSegmentsChanges)
            {
                changes.Add("Media segments");
            }

            return string.Join(", ", changes);
        }
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
