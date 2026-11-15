using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Services.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.UserSync;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using JPKribs.Jellyfin.Base;
using LocalImageType = MediaBrowser.Model.Entities.ImageType;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.ServerSync.Services.PeopleSync;

/// <summary>
/// Builds the comparable source and local form of a person's metadata and images.
/// </summary>
public static class PeopleSyncMergeService
{
    /// <summary>
    /// The local image types read into a person's manifest, with Backdrop because a person can hold several.
    /// </summary>
    private static readonly LocalImageType[] LocalImageTypes =
    {
        LocalImageType.Primary,
        LocalImageType.Backdrop
    };

    /// <summary>
    /// Builds the metadata blob of a source person, with lists sorted and dates as calendar dates so both sides serialize alike.
    /// Name is left out because it is the match key and Jellyfin recreates a person whose casing is rewritten, and SortName because each server derives it.
    /// </summary>
    /// <param name="sourcePerson">The person as the source returned it.</param>
    /// <returns>The metadata as JSON.</returns>
    public static string BuildSourceMetadata(BaseItemDto sourcePerson)
    {
        ArgumentNullException.ThrowIfNull(sourcePerson);

        var sourceProviderIds = JsonFieldHelpers.CollapseProviderIds(sourcePerson.ProviderIds?.AdditionalData?
            .Select(kvp => new KeyValuePair<string, string>(kvp.Key, MediaItemUtilities.UnwrapKiotaPrimitive(kvp.Value) ?? string.Empty)));

        var metadata = new Dictionary<string, object?>
        {
            ["OriginalTitle"] = sourcePerson.OriginalTitle,
            ["ForcedSortName"] = sourcePerson.ForcedSortName,
            ["Overview"] = sourcePerson.Overview,
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
    /// Builds the metadata blob of a local person, with the same keys and ordering as <see cref="BuildSourceMetadata"/>.
    /// </summary>
    /// <param name="localPerson">The local person to read.</param>
    /// <returns>The metadata as JSON.</returns>
    public static string BuildLocalMetadata(BaseItem localPerson)
    {
        ArgumentNullException.ThrowIfNull(localPerson);

        var localProviderIds = JsonFieldHelpers.CollapseProviderIds(localPerson.ProviderIds);

        var metadata = new Dictionary<string, object?>
        {
            ["OriginalTitle"] = localPerson.OriginalTitle,
            ["ForcedSortName"] = localPerson.ForcedSortName,
            ["Overview"] = localPerson.Overview,
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
    /// Builds a person's source image manifest from its image tags, and its local one from the image files.
    /// </summary>
    /// <param name="sourcePerson">The person as the source returned it, or null.</param>
    /// <param name="localPerson">The local person, or null.</param>
    /// <returns>The source and local manifests, each null when its person is null.</returns>
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

            // A person with only backdrops has no ImageTags, so this stands apart.
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
                        // An unreadable image keeps a size of 0.
                        catch (IOException)
                        {
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
}
