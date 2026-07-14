using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;

namespace Jellyfin.Plugin.ServerSync.Models;

/// <summary>
/// Mapping helpers for <see cref="MetadataSyncItem"/>.
/// </summary>
public static class MetadataSyncItemExtensions
{
    /// <summary>Projects a metadata sync item to its API DTO.</summary>
    /// <param name="item">The metadata sync item.</param>
    /// <param name="libraryMappings">The configured library mappings, used to resolve library names.</param>
    /// <param name="sourceServerUrl">The source server URL surfaced to the client.</param>
    /// <param name="includeBlobs">When false the large JSON blob fields are omitted, for list views.</param>
    /// <returns>The DTO representation.</returns>
    public static MetadataSyncItemDto ToDto(
        this MetadataSyncItem item,
        List<LibraryMapping>? libraryMappings,
        string? sourceServerUrl,
        bool includeBlobs = true)
    {
        string? sourceLibraryName = null;
        string? localLibraryName = null;

        var mapping = libraryMappings?.FirstOrDefault(m => m.SourceLibraryId == item.SourceLibraryId);
        if (mapping != null)
        {
            sourceLibraryName = mapping.SourceLibraryName;
            localLibraryName = mapping.LocalLibraryName;
        }

        var hasMetadataChanges = item.HasMetadataChanges;
        var hasImagesChanges = item.HasImagesChanges;
        var hasPeopleChanges = item.HasPeopleChanges;
        var hasStudiosChanges = item.HasStudiosChanges;
        var changesSummary = item.ChangesSummary;

        return new MetadataSyncItemDto
        {
            Id = item.Id,
            SourceLibraryId = item.SourceLibraryId,
            LocalLibraryId = item.LocalLibraryId,
            SourceLibraryName = sourceLibraryName,
            LocalLibraryName = localLibraryName,
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
            HasMetadataChanges = hasMetadataChanges,
            HasImagesChanges = hasImagesChanges,
            HasPeopleChanges = hasPeopleChanges,
            HasStudiosChanges = hasStudiosChanges,
            HasChanges = hasMetadataChanges || hasImagesChanges || hasPeopleChanges || hasStudiosChanges,
            ChangesSummary = changesSummary,
            SourceServerUrl = sourceServerUrl,
            Status = item.Status.ToString(),
            StatusDate = item.StatusDate,
            LastSyncTime = item.LastSyncTime,
            ErrorMessage = item.Reason
        };
    }
}
