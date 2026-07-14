using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using JPKribs.Jellyfin.Base;
using LocalImageType = MediaBrowser.Model.Entities.ImageType;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Per-category merge logic for People Sync. Builds the comparable
/// representations of a person's metadata + images on the source and local
/// sides so the <see cref="SyncableValue{T}"/> comparators have honest
/// inputs. Static class matching <see cref="HistorySyncMergeService"/>,
/// <see cref="MetadataSyncMergeService"/>, and <see cref="UserSyncMergeService"/>.
/// </summary>
public static class PeopleSyncMergeService
{
    // ===================================================================
    // Per-category source + local blob building
    // ===================================================================

    /// <summary>
    /// Builds a metadata JSON blob from a source <see cref="BaseItemDto"/>
    /// (person). Mirrors <see cref="MetadataSyncMergeService.MergeMetadataFields"/>'s
    /// shape so source and local blobs serialize identically (sorted arrays,
    /// unwrapped Kiota primitives, normalized strings).
    /// </summary>
    public static string BuildSourceMetadata(BaseItemDto sourcePerson)
    {
        ArgumentNullException.ThrowIfNull(sourcePerson);

        // Unwrap Kiota's UntypedNode wrappers — calling .ToString() directly on
        // an AdditionalData entry yields the type name, not the wrapped value,
        // which made every ProviderId look like "UntypedString" and produced a
        // permanent IMDB desync in the modal.
        var sourceProviderIds = sourcePerson.ProviderIds?.AdditionalData?
            .Select(kvp => (kvp.Key, Value: MediaItemUtilities.UnwrapKiotaPrimitive(kvp.Value)))
            .Where(kvp => !string.IsNullOrEmpty(kvp.Value))
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!);

        // Sort all order-insensitive arrays so source and local serialize
        // identically. The JSON comparator compares element-by-element and
        // would otherwise flag differently-ordered-but-same-content arrays
        // as "changed" forever, even after a successful apply.
        var metadata = new Dictionary<string, object?>
        {
            ["Name"] = sourcePerson.Name,
            ["OriginalTitle"] = sourcePerson.OriginalTitle,
            // SortName excluded — server-derived from Name, never matches
            // across servers. ForcedSortName is the user override and IS
            // synced.
            ["ForcedSortName"] = sourcePerson.ForcedSortName,
            ["Overview"] = sourcePerson.Overview,
            // Birth/death dates stored date-only: the apply step syncs these
            // as calendar dates (DateOnlyEquals), so the blobs must not carry
            // time-of-day or comparison and apply disagree forever.
            ["PremiereDate"] = JsonComparisonUtility.ToDateOnlyString(sourcePerson.PremiereDate),
            ["EndDate"] = JsonComparisonUtility.ToDateOnlyString(sourcePerson.EndDate),
            ["ProductionYear"] = sourcePerson.ProductionYear,
            ["ProductionLocations"] = StringNormalizationUtility.NormalizeStringArray(sourcePerson.ProductionLocations),
            ["Tags"] = StringNormalizationUtility.NormalizeStringArray(sourcePerson.Tags),
            ["ProviderIds"] = sourceProviderIds,
            ["LockedFields"] = sourcePerson.LockedFields?.Select(f => f.ToString())
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ["LockData"] = sourcePerson.LockData ?? false
        };

        return JsonSerializer.Serialize(metadata);
    }

    /// <summary>
    /// Builds a metadata JSON blob from a local <see cref="BaseItem"/>
    /// (person). Symmetric with <see cref="BuildSourceMetadata"/> — same
    /// keys, same sort order, same normalization.
    /// </summary>
    public static string BuildLocalMetadata(BaseItem localPerson)
    {
        ArgumentNullException.ThrowIfNull(localPerson);

        var localProviderIds = localPerson.ProviderIds?
            .Where(kvp => !string.IsNullOrEmpty(kvp.Value))
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        var metadata = new Dictionary<string, object?>
        {
            ["Name"] = localPerson.Name,
            ["OriginalTitle"] = localPerson.OriginalTitle,
            // SortName excluded — see source-side comment.
            ["ForcedSortName"] = localPerson.ForcedSortName,
            ["Overview"] = localPerson.Overview,
            // Date-only — see source-side comment in BuildSourceMetadata.
            ["PremiereDate"] = JsonComparisonUtility.ToDateOnlyString(localPerson.PremiereDate),
            ["EndDate"] = JsonComparisonUtility.ToDateOnlyString(localPerson.EndDate),
            ["ProductionYear"] = localPerson.ProductionYear,
            ["ProductionLocations"] = StringNormalizationUtility.NormalizeStringArray(localPerson.ProductionLocations),
            ["Tags"] = StringNormalizationUtility.NormalizeStringArray(localPerson.Tags),
            ["ProviderIds"] = localProviderIds,
            ["LockedFields"] = localPerson.LockedFields?.Select(f => f.ToString())
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ["LockData"] = localPerson.IsLocked
        };

        return JsonSerializer.Serialize(metadata);
    }

    /// <summary>
    /// Builds the source-side and local-side image manifests for a person.
    /// Source side comes from <see cref="BaseItemDto.ImageTags"/> returned
    /// by the bulk <c>/Persons</c> fetch (tag-only — no per-person HTTP);
    /// local side reads on-disk image dimensions and size. Caller pipes the
    /// result through <see cref="SyncableValue{T}.UpdateSource"/> so the
    /// comparator handles hashing uniformly.
    /// </summary>
    public static (string? SourceImagesValue, string? LocalImagesValue) PopulateImageData(
        BaseItemDto? sourcePerson,
        BaseItem? localPerson)
    {
        string? sourceImagesValue = null;
        string? localImagesValue = null;

        if (sourcePerson?.ImageTags?.AdditionalData != null)
        {
            var sourceImagesByType = new Dictionary<string, List<ImageInfoDto>>();
            foreach (var kvp in sourcePerson.ImageTags.AdditionalData)
            {
                var tag = MediaItemUtilities.UnwrapKiotaPrimitive(kvp.Value);
                if (string.IsNullOrEmpty(tag))
                {
                    continue;
                }

                sourceImagesByType[kvp.Key] = new List<ImageInfoDto>
                {
                    new ImageInfoDto
                    {
                        ImageType = kvp.Key,
                        ImageIndex = 0,
                        Tag = tag
                    }
                };
            }

            if (sourceImagesByType.Count > 0)
            {
                sourceImagesValue = JsonSerializer.Serialize(sourceImagesByType);
            }
        }

        // Local images — people typically only have Primary images.
        if (localPerson != null)
        {
            var localImagesByType = new Dictionary<string, List<ImageInfoDto>>();
            var images = localPerson.GetImages(LocalImageType.Primary).ToList();
            if (images.Count > 0)
            {
                var imageInfoList = new List<ImageInfoDto>();
                for (int idx = 0; idx < images.Count; idx++)
                {
                    var img = images[idx];
                    long fileSize = 0;

                    if (!string.IsNullOrEmpty(img.Path) && File.Exists(img.Path))
                    {
                        try
                        {
                            fileSize = new FileInfo(img.Path).Length;
                        }
                        catch (IOException)
                        {
                            // Ignore file access errors
                        }
                    }

                    imageInfoList.Add(new ImageInfoDto
                    {
                        ImageType = LocalImageType.Primary.ToString(),
                        ImageIndex = idx,
                        Size = fileSize,
                        Width = img.Width,
                        Height = img.Height
                    });
                }

                localImagesByType[LocalImageType.Primary.ToString()] = imageInfoList;
            }

            if (localImagesByType.Count > 0)
            {
                localImagesValue = JsonSerializer.Serialize(localImagesByType);
            }
        }

        return (sourceImagesValue, localImagesValue);
    }

    // ===================================================================
    // Parity helpers (match the other *SyncMergeService surfaces)
    // ===================================================================

    /// <summary>
    /// True if any enabled category on the record has changes worth syncing.
    /// Mirrors <see cref="HistorySyncMergeService.HasChangesToSync"/>.
    /// </summary>
    public static bool HasChangesToSync(PeopleSyncItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.HasChanges;
    }

    /// <summary>
    /// Human-readable summary of which categories changed, for logs / modals.
    /// Mirrors <see cref="HistorySyncMergeService.GetChangeSummary"/>.
    /// </summary>
    public static string GetChangeSummary(PeopleSyncItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var changes = new List<string>();
        if (item.HasMetadataChanges) changes.Add("Metadata");
        if (item.HasImagesChanges) changes.Add("Images");

        return changes.Count > 0 ? string.Join(", ", changes) : "No changes";
    }
}
