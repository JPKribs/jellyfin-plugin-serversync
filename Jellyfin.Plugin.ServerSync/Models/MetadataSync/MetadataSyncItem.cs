using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Common.Comparators;
using Jellyfin.Plugin.ServerSync.Utilities;

namespace Jellyfin.Plugin.ServerSync.Models.MetadataSync;

/// <summary>
/// One sync record per (source library, source item). Carries four parallel
/// JSON-blob fields — <see cref="Metadata"/>, <see cref="Images"/>,
/// <see cref="People"/>, <see cref="Studios"/> — each with its own
/// Source/Local/Synced snapshots and content hashes for the change-detection
/// short-circuit. Refresh populates the source side and recomputes hashes;
/// Sync applies the source values to local and calls
/// <see cref="SyncableValue{T}.MarkSynced"/> on each successfully-applied
/// category.
/// </summary>
public class MetadataSyncItem : SyncRecord
{
    // ===== Item Identification =====

    /// <summary>
    /// Gets or sets the source library ID — first component of the natural key.
    /// </summary>
    public string SourceLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local library ID.
    /// </summary>
    public string LocalLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source server item ID — second component of the
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

    // ===== Syncable JSON-blob categories =====

    /// <summary>
    /// Gets the metadata blob (name, overview, ratings, dates, genres, tags,
    /// provider IDs, etc.). JSON-equality comparison.
    /// </summary>
    public SyncableValue<string> Metadata { get; } = new() { Comparator = new JsonBlobComparator() };

    /// <summary>
    /// Gets the image manifest. Per-type counts and file sizes; uses the
    /// dedicated <see cref="ImageManifestComparator"/>.
    /// </summary>
    public SyncableValue<string> Images { get; } = new() { Comparator = new ImageManifestComparator() };

    /// <summary>
    /// Gets the people array (cast / crew, by Name+Role+Type). JSON-equality
    /// comparison.
    /// </summary>
    public SyncableValue<string> People { get; } = new() { Comparator = new JsonBlobComparator() };

    /// <summary>
    /// Gets the studios array (just studio names). JSON-equality comparison.
    /// </summary>
    public SyncableValue<string> Studios { get; } = new() { Comparator = new JsonBlobComparator() };

    // ===== Per-category change detection =====

    /// <summary>
    /// Gets a value indicating whether the metadata blob has changes worth
    /// syncing. False until a local match exists, since metadata can't be
    /// applied without a local item.
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
    {
        get
        {
            if (string.IsNullOrEmpty(LocalItemId))
            {
                return false;
            }

            if (string.IsNullOrEmpty(Studios.Source) || Studios.Source == "[]")
            {
                return false;
            }

            return Studios.HasChanges;
        }
    }

    /// <inheritdoc />
    public override bool HasChanges => HasMetadataChanges || HasImagesChanges || HasPeopleChanges || HasStudiosChanges;

    /// <inheritdoc />
    public override void MarkSynced()
    {
        Metadata.MarkSynced();
        Images.MarkSynced();
        People.MarkSynced();
        Studios.MarkSynced();
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

            return string.Join(", ", changes);
        }
    }

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
