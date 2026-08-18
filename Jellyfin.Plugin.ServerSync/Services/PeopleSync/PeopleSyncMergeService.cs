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
    /// <summary>
    /// Local image types read into a person's manifest. Primary is the usual
    /// case, and Backdrop is here because it is the one type a person can
    /// hold several of.
    /// </summary>
    private static readonly LocalImageType[] LocalImageTypes =
    {
        LocalImageType.Primary,
        LocalImageType.Backdrop
    };

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
    /// Builds the source side and local side image manifests for a person.
    /// Source side reads <see cref="BaseItemDto.ImageTags"/> and
    /// <see cref="BaseItemDto.BackdropImageTags"/>, and local side reads the
    /// on disk size and dimensions for the same types.
    /// </summary>
    public static (string? SourceImagesValue, string? LocalImagesValue) PopulateImageData(
        BaseItemDto? sourcePerson,
        BaseItem? localPerson)
    {
        string? sourceImagesValue = null;
        string? localImagesValue = null;

        if (sourcePerson != null)
        {
            var sourceImagesByType = new Dictionary<string, List<ImageInfoDto>>();
            if (sourcePerson.ImageTags?.AdditionalData != null)
            {
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
            }

            // Backdrops arrive as their own tag list, not as an ImageTags
            // entry, and there can be more than one. This sits outside the
            // ImageTags guard because a person whose only images are
            // backdrops has no ImageTags at all, and skipping them there
            // would leave the manifest null and the backdrops permanently
            // unsynced. It is assigned last so it wins over any stray single
            // tag Backdrop entry above.
            if (sourcePerson.BackdropImageTags?.Count > 0)
            {
                sourceImagesByType["Backdrop"] = sourcePerson.BackdropImageTags
                    .Select((tag, idx) => new ImageInfoDto { ImageType = "Backdrop", ImageIndex = idx, Tag = tag })
                    .ToList();
            }

            if (sourceImagesByType.Count > 0)
            {
                sourceImagesValue = JsonSerializer.Serialize(sourceImagesByType);
            }
        }

        // Local images. People usually carry only a Primary, but Backdrop
        // has to be read too. Otherwise a source with several backdrops has
        // nothing to compare against and the apply can never be verified.
        if (localPerson != null)
        {
            var localImagesByType = new Dictionary<string, List<ImageInfoDto>>();
            foreach (var imageType in LocalImageTypes)
            {
                var images = localPerson.GetImages(imageType).ToList();
                if (images.Count == 0)
                {
                    continue;
                }

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
                        ImageType = imageType.ToString(),
                        ImageIndex = idx,
                        Size = fileSize,
                        Width = img.Width,
                        Height = img.Height
                    });
                }

                localImagesByType[imageType.ToString()] = imageInfoList;
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
