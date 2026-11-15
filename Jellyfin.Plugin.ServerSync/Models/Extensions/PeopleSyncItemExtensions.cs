using System;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Common.Comparators;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;

namespace Jellyfin.Plugin.ServerSync.Models.Extensions;

/// <summary>
/// Mapping helpers for <see cref="PeopleSyncItem"/>.
/// </summary>
public static class PeopleSyncItemExtensions
{
    /// <summary>Projects a people sync item to its API DTO.</summary>
    /// <param name="item">The row to show.</param>
    /// <param name="sourceServerUrl">The source's URL the browser loads images and links from.</param>
    /// <returns>The row as the dashboard reads it.</returns>
    public static PeopleSyncItemDto ToDto(this PeopleSyncItem item, string? sourceServerUrl)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new PeopleSyncItemDto
        {
            Id = item.Id,
            PersonName = item.PersonName,
            SourcePersonId = item.SourcePersonId,
            LocalPersonId = item.LocalPersonId,
            SourceMetadataValue = item.Metadata.Source,
            LocalMetadataValue = item.Metadata.Local,
            SourceImagesValue = item.Images.Source,
            LocalImagesValue = item.Images.Local,
            HasMetadataChanges = item.HasMetadataChanges,
            HasImagesChanges = item.HasImagesChanges,
            ImagesChangesDetail = item.HasImagesChanges
                ? (item.Images.Comparator as ImageManifestComparator)?.DescribeDifference(item.Images.Source, item.Images.Local)
                : null,
            MetadataChangesDetail = item.HasMetadataChanges
                ? string.Join(", ", JsonComparisonUtility.GetDifferingFields(item.Metadata.Source, item.Metadata.Local))
                : null,
            HasChanges = item.HasChanges,
            Status = item.Status.ToString(),
            StatusDate = item.StatusDate,
            LastSyncTime = item.LastSyncTime,
            ErrorMessage = item.Reason,
            SourceServerUrl = sourceServerUrl,
            ServerKey = item.ServerKey,
        };
    }
}
