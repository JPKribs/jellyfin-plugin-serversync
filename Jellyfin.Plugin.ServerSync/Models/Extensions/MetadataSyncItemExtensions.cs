using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Common.Comparators;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;

namespace Jellyfin.Plugin.ServerSync.Models.Extensions;

/// <summary>
/// Mapping helpers for <see cref="MetadataSyncItem"/>.
/// </summary>
public static class MetadataSyncItemExtensions
{
    /// <summary>Projects a metadata sync item to its API DTO.</summary>
    /// <param name="item">The row to show.</param>
    /// <param name="libraryMappings">The library mappings, used to resolve library names.</param>
    /// <param name="sourceServerUrl">The source's URL the browser loads images and links from.</param>
    /// <param name="includeBlobs">Whether to include the large JSON blobs, which list views leave out.</param>
    /// <returns>The row as the dashboard reads it.</returns>
    public static MetadataSyncItemDto ToDto(
        this MetadataSyncItem item,
        List<LibraryMapping>? libraryMappings,
        string? sourceServerUrl,
        bool includeBlobs = true)
    {
        ArgumentNullException.ThrowIfNull(item);

        var mapping = libraryMappings?.FirstOrDefault(m => m.SourceLibraryId == item.SourceLibraryId);
        var hasMetadataChanges = item.HasMetadataChanges;
        var hasImagesChanges = item.HasImagesChanges;
        var hasPeopleChanges = item.HasPeopleChanges;
        var hasStudiosChanges = item.HasStudiosChanges;
        var hasSegmentsChanges = item.HasSegmentsChanges;
        var changesSummary = item.ChangesSummary;

        return new MetadataSyncItemDto
        {
            Id = item.Id,
            SourceLibraryId = item.SourceLibraryId,
            LocalLibraryId = item.LocalLibraryId,
            SourceLibraryName = mapping?.SourceLibraryName,
            LocalLibraryName = mapping?.LocalLibraryName,
            SourceItemId = item.SourceItemId,
            LocalItemId = item.LocalItemId,
            ItemName = item.ItemName,
            SourcePath = item.SourcePath,
            LocalPath = item.LocalPath,
            SourceMetadataValue = includeBlobs ? item.Metadata.Source : null,
            LocalMetadataValue = includeBlobs ? item.Metadata.Local : null,
            SourceImagesValue = includeBlobs ? item.Images.Source : null,
            LocalImagesValue = includeBlobs ? item.Images.Local : null,
            SourcePeopleValue = includeBlobs ? item.People.Source : null,
            LocalPeopleValue = includeBlobs ? item.People.Local : null,
            SourceStudiosValue = includeBlobs ? item.Studios.Source : null,
            LocalStudiosValue = includeBlobs ? item.Studios.Local : null,
            SourceSegmentsValue = includeBlobs ? item.Segments.Source : null,
            LocalSegmentsValue = includeBlobs ? item.Segments.Local : null,
            HasMetadataChanges = hasMetadataChanges,
            HasImagesChanges = hasImagesChanges,
            ImagesChangesDetail = includeBlobs && hasImagesChanges
                ? (item.Images.Comparator as ImageManifestComparator)?.DescribeDifference(item.Images.Source, item.Images.Local)
                : null,
            MetadataChangesDetail = includeBlobs && hasMetadataChanges
                ? string.Join(", ", JsonComparisonUtility.GetDifferingFields(item.Metadata.Source, item.Metadata.Local))
                : null,
            HasPeopleChanges = hasPeopleChanges,
            HasStudiosChanges = hasStudiosChanges,
            HasSegmentsChanges = hasSegmentsChanges,
            HasChanges = hasMetadataChanges || hasImagesChanges || hasPeopleChanges || hasStudiosChanges || hasSegmentsChanges,
            ChangesSummary = changesSummary,
            SourceServerUrl = sourceServerUrl,
            ServerKey = item.ServerKey,
            Status = item.Status.ToString(),
            StatusDate = item.StatusDate,
            LastSyncTime = item.LastSyncTime,
            ErrorMessage = item.Reason
        };
    }
}
