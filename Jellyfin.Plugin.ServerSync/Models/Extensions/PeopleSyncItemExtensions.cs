using Jellyfin.Plugin.ServerSync.Models.PeopleSync;

namespace Jellyfin.Plugin.ServerSync.Models;

/// <summary>
/// Mapping helpers for <see cref="PeopleSyncItem"/>.
/// </summary>
public static class PeopleSyncItemExtensions
{
    /// <summary>Projects a people sync item to its API DTO.</summary>
    /// <param name="item">The people sync item.</param>
    /// <param name="sourceServerUrl">The source server URL surfaced to the client.</param>
    /// <param name="sourceServerApiKey">The source server API key surfaced to the client.</param>
    /// <returns>The DTO representation.</returns>
    public static PeopleSyncItemDto ToDto(this PeopleSyncItem item, string? sourceServerUrl, string? sourceServerApiKey)
    {
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
            HasChanges = item.HasChanges,
            Status = item.Status.ToString(),
            StatusDate = item.StatusDate,
            LastSyncTime = item.LastSyncTime,
            ErrorMessage = item.Reason,
            SourceServerUrl = sourceServerUrl,
            SourceServerApiKey = sourceServerApiKey
        };
    }
}
