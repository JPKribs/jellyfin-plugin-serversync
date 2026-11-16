using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.MetadataSync;

/// <summary>
/// Builds the comparable source and local form of each metadata category on a <see cref="MetadataSyncItem"/>, where the source wins.
/// </summary>
public static class MetadataSyncMergeService
{
    /// <summary>
    /// The local image types read into an item's manifest.
    /// </summary>
    public static readonly IReadOnlyList<MediaBrowser.Model.Entities.ImageType> ImageTypes = new[]
    {
        MediaBrowser.Model.Entities.ImageType.Primary,
        MediaBrowser.Model.Entities.ImageType.Backdrop,
        MediaBrowser.Model.Entities.ImageType.Logo,
        MediaBrowser.Model.Entities.ImageType.Thumb,
        MediaBrowser.Model.Entities.ImageType.Banner,
        MediaBrowser.Model.Entities.ImageType.Art,
        MediaBrowser.Model.Entities.ImageType.Disc
    };

    /// <summary>
    /// The source image types left out of an item's manifest. Jellyfin drops a Profile image on a media item, so tracking one would error forever.
    /// </summary>
    public static readonly IReadOnlyCollection<string> UnsavableImageTypes = new[] { "Profile" };

    /// <summary>
    /// Builds the source and local metadata blobs, holding only fields that sync so the compare and the hash match what is written.
    /// SortName is left out because each server derives it from Name, dates are calendar dates, and lists are sorted so both sides serialize alike.
    /// </summary>
    /// <param name="item">The row whose metadata blobs are built.</param>
    /// <param name="sourceItem">The item as the source returned it.</param>
    /// <param name="localItem">The matching local item, or null when there is none.</param>
    /// <param name="syncGenres"><c>true</c> to include genres in both blobs.</param>
    /// <param name="syncTags"><c>true</c> to include tags in both blobs.</param>
    public static void MergeMetadataFields(
        MetadataSyncItem item,
        BaseItemDto sourceItem,
        BaseItem? localItem,
        bool syncGenres,
        bool syncTags)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(sourceItem);

        var sourceProviderIds = JsonFieldHelpers.CollapseProviderIds(sourceItem.ProviderIds?.AdditionalData?
            .Select(kvp => new KeyValuePair<string, string>(kvp.Key, MediaItemUtilities.UnwrapKiotaPrimitive(kvp.Value) ?? string.Empty)));

        var sourceMetadata = new Dictionary<string, object?>
        {
            ["Name"] = sourceItem.Name,
            ["OriginalTitle"] = sourceItem.OriginalTitle,
            ["ForcedSortName"] = sourceItem.ForcedSortName,
            ["Overview"] = sourceItem.Overview,
            ["Tagline"] = sourceItem.Taglines?.FirstOrDefault(),
            ["OfficialRating"] = sourceItem.OfficialRating,
            ["CustomRating"] = sourceItem.CustomRating,
            ["CommunityRating"] = sourceItem.CommunityRating,
            ["CriticRating"] = sourceItem.CriticRating,
            ["PremiereDate"] = JsonComparisonUtility.ToDateOnlyString(sourceItem.PremiereDate),
            ["EndDate"] = JsonComparisonUtility.ToDateOnlyString(sourceItem.EndDate),
            ["ProductionYear"] = sourceItem.ProductionYear,
            ["ProviderIds"] = sourceProviderIds,
            ["IndexNumber"] = sourceItem.IndexNumber,
            ["ParentIndexNumber"] = sourceItem.ParentIndexNumber,
            ["PreferredMetadataCountryCode"] = sourceItem.PreferredMetadataCountryCode,
            ["PreferredMetadataLanguage"] = sourceItem.PreferredMetadataLanguage,
            ["AspectRatio"] = sourceItem.AspectRatio,
            ["Video3DFormat"] = sourceItem.Video3DFormat?.ToString(),
            ["LockedFields"] = sourceItem.LockedFields?.Select(f => f.ToString())
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ["LockData"] = sourceItem.LockData ?? false
        };

        if (syncGenres)
        {
            sourceMetadata["Genres"] = StringNormalizationUtility.NormalizeStringArray(sourceItem.Genres);
        }

        if (syncTags)
        {
            sourceMetadata["Tags"] = StringNormalizationUtility.NormalizeStringArray(sourceItem.Tags);
        }

        item.Metadata.UpdateSource(JsonSerializer.Serialize(sourceMetadata));

        if (localItem != null)
        {
            RebuildLocalMetadataBlob(item, localItem, syncGenres, syncTags);
        }
    }

    /// <summary>
    /// Builds the source and local image manifests, with the source side read from the item's image tags.
    /// Sizes carry over from <paramref name="priorSourceManifestJson"/> while the tags match, so only a changed item costs a call to the source.
    /// </summary>
    /// <param name="item">The row whose image manifests are built.</param>
    /// <param name="sourceItem">The item as the source returned it.</param>
    /// <param name="localItem">The matching local item, or null when there is none.</param>
    /// <param name="client">The client for the source server, or null to skip sizing.</param>
    /// <param name="priorSourceManifestJson">The last scan's source manifest, or null.</param>
    /// <param name="priorLocalManifestJson">The last scan's local manifest, which holds the tags matched so far, or null.</param>
    /// <param name="deepVerification"><c>true</c> to read each image's size from the source.</param>
    /// <param name="logger">Writes a debug entry when sizing fails.</param>
    /// <param name="cancellationToken">Cancels the calls that size the source images.</param>
    /// <returns>A task that completes when both manifests are set.</returns>
    public static async Task MergeImagesAsync(
        MetadataSyncItem item,
        BaseItemDto sourceItem,
        BaseItem? localItem,
        SourceServerClient? client,
        string? priorSourceManifestJson,
        string? priorLocalManifestJson,
        bool deepVerification,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(sourceItem);
        ArgumentNullException.ThrowIfNull(logger);

        var (sourceManifestJson, localManifestJson) = await ImageUtilities.ReadManifestsAsync(
            sourceItem,
            localItem,
            ImageTypes,
            priorSourceManifestJson,
            priorLocalManifestJson,
            Guid.TryParse(item.SourceItemId, out var sourceItemGuid) ? sourceItemGuid : null,
            client,
            deepVerification,
            UnsavableImageTypes,
            logger,
            item.ItemName ?? item.SourceItemId,
            cancellationToken).ConfigureAwait(false);

        item.Images.UpdateSource(sourceManifestJson);

        if (localItem != null)
        {
            item.Images.Local = localManifestJson;
        }
    }

    /// <summary>
    /// Builds the source and local people blobs by name, role, and type, since person ids differ between servers.
    /// Both sides keep billing order so the cast order syncs too.
    /// </summary>
    /// <param name="item">The row whose people blobs are built.</param>
    /// <param name="sourceItem">The item as the source returned it.</param>
    /// <param name="localItem">The matching local item, or null when there is none.</param>
    /// <param name="libraryManager">Reads the local item's people, and is unused when there is no local item.</param>
    public static void MergePeople(
        MetadataSyncItem item,
        BaseItemDto sourceItem,
        BaseItem? localItem,
        ILibraryManager libraryManager)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(sourceItem);

        if (sourceItem.People != null && sourceItem.People.Count > 0)
        {
            var sourcePeople = new List<Dictionary<string, string>>();
            foreach (var person in sourceItem.People)
            {
                var personDict = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(person.Name))
                {
                    personDict["Name"] = person.Name;
                }

                if (!string.IsNullOrEmpty(person.Role))
                {
                    personDict["Role"] = person.Role;
                }

                if (person.Type != null)
                {
                    personDict["Type"] = person.Type.Value.ToString();
                }

                if (personDict.ContainsKey("Name"))
                {
                    sourcePeople.Add(personDict);
                }
            }

            item.People.UpdateSource(JsonSerializer.Serialize(sourcePeople));
        }
        else
        {
            item.People.UpdateSource("[]");
        }

        if (localItem != null)
        {
            RebuildLocalPeopleBlob(item, localItem, libraryManager);
        }
        else
        {
            item.People.Local = null;
        }
    }

    /// <summary>
    /// Builds the source and local studios blobs by name, since studio ids differ between servers.
    /// Both sides drop blank names so they never differ on one.
    /// </summary>
    /// <param name="item">The row whose studios blobs are built.</param>
    /// <param name="sourceItem">The item as the source returned it.</param>
    /// <param name="localItem">The matching local item, or null when there is none.</param>
    public static void MergeStudios(
        MetadataSyncItem item,
        BaseItemDto sourceItem,
        BaseItem? localItem)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(sourceItem);

        if (sourceItem.Studios != null && sourceItem.Studios.Count > 0)
        {
            var studioNames = sourceItem.Studios
                .Select(s => s?.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            item.Studios.UpdateSource(studioNames.Count > 0
                ? JsonSerializer.Serialize(studioNames)
                : "[]");
        }
        else
        {
            item.Studios.UpdateSource("[]");
        }

        if (localItem != null)
        {
            RebuildLocalStudiosBlob(item, localItem);
        }
        else
        {
            item.Studios.Local = null;
        }
    }

    /// <summary>
    /// Rebuilds the local metadata blob from the live local item, with the same fields as the source blob.
    /// </summary>
    /// <param name="item">The row whose local metadata blob is replaced.</param>
    /// <param name="localItem">The live local item to read.</param>
    /// <param name="syncGenres"><c>true</c> to include genres.</param>
    /// <param name="syncTags"><c>true</c> to include tags.</param>
    public static void RebuildLocalMetadataBlob(MetadataSyncItem item, BaseItem localItem, bool syncGenres, bool syncTags)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(localItem);

        var localVideo = localItem as MediaBrowser.Controller.Entities.Video;

        var localMetadata = new Dictionary<string, object?>
        {
            ["Name"] = localItem.Name,
            ["OriginalTitle"] = localItem.OriginalTitle,
            ["ForcedSortName"] = localItem.ForcedSortName,
            ["Overview"] = localItem.Overview,
            ["Tagline"] = localItem.Tagline,
            ["OfficialRating"] = localItem.OfficialRating,
            ["CustomRating"] = localItem.CustomRating,
            ["CommunityRating"] = localItem.CommunityRating,
            ["CriticRating"] = localItem.CriticRating,
            ["PremiereDate"] = JsonComparisonUtility.ToDateOnlyString(localItem.PremiereDate),
            ["EndDate"] = JsonComparisonUtility.ToDateOnlyString(localItem.EndDate),
            ["ProductionYear"] = localItem.ProductionYear,
            ["ProviderIds"] = JsonFieldHelpers.CollapseProviderIds(localItem.ProviderIds),
            ["IndexNumber"] = localItem.IndexNumber,
            ["ParentIndexNumber"] = localItem.ParentIndexNumber,
            ["PreferredMetadataCountryCode"] = localItem.PreferredMetadataCountryCode,
            ["PreferredMetadataLanguage"] = localItem.PreferredMetadataLanguage,
            ["AspectRatio"] = localVideo?.AspectRatio,
            ["Video3DFormat"] = localVideo?.Video3DFormat?.ToString(),
            ["LockedFields"] = localItem.LockedFields?.Select(f => f.ToString())
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ["LockData"] = localItem.IsLocked
        };

        if (syncGenres)
        {
            localMetadata["Genres"] = StringNormalizationUtility.NormalizeStringArray(localItem.Genres);
        }

        if (syncTags)
        {
            localMetadata["Tags"] = StringNormalizationUtility.NormalizeStringArray(localItem.Tags);
        }

        item.Metadata.Local = JsonSerializer.Serialize(localMetadata);
    }

    /// <summary>
    /// Rebuilds the local image manifest from the local item's image files.
    /// </summary>
    /// <param name="item">The row whose local image manifest is replaced.</param>
    /// <param name="localItem">The live local item whose images are read.</param>
    public static void RebuildLocalImagesBlob(MetadataSyncItem item, BaseItem localItem)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(localItem);

        item.Images.Local = ImageUtilities.BuildLocalManifest(localItem, ImageTypes, item.Images.Local);
    }

    /// <summary>
    /// Rebuilds the local people blob in the order the source lists people.
    /// </summary>
    /// <param name="item">The row whose local people blob is replaced.</param>
    /// <param name="localItem">The live local item whose people are read.</param>
    /// <param name="libraryManager">Reads the local item's people.</param>
    public static void RebuildLocalPeopleBlob(MetadataSyncItem item, BaseItem localItem, ILibraryManager libraryManager)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(localItem);
        ArgumentNullException.ThrowIfNull(libraryManager);

        var localPeopleList = libraryManager.GetPeople(localItem);
        if (localPeopleList == null || localPeopleList.Count == 0)
        {
            item.People.Local = "[]";
            return;
        }

        var localPeople = new List<Dictionary<string, string>>();
        foreach (var person in OrderLikeSourceDto(localPeopleList).Where(p => !string.IsNullOrEmpty(p.Name)))
        {
            var personDict = new Dictionary<string, string> { ["Name"] = person.Name };
            if (!string.IsNullOrEmpty(person.Role)) personDict["Role"] = person.Role;
            personDict["Type"] = person.Type.ToString();
            localPeople.Add(personDict);
        }

        item.People.Local = JsonSerializer.Serialize(localPeople);
    }

    /// <summary>
    /// Rebuilds the local studios blob from the local item's studio names.
    /// </summary>
    /// <param name="item">The row whose local studios blob is replaced.</param>
    /// <param name="localItem">The live local item whose studios are read.</param>
    public static void RebuildLocalStudiosBlob(MetadataSyncItem item, BaseItem localItem)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(localItem);

        if (localItem.Studios == null || localItem.Studios.Length == 0)
        {
            item.Studios.Local = "[]";
            return;
        }

        var validStudios = localItem.Studios
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        item.Studios.Local = validStudios.Count > 0
            ? JsonSerializer.Serialize(validStudios)
            : "[]";
    }

    /// <summary>
    /// Orders local people the way Jellyfin orders the people a source returns, by sort order and then by a fixed rank of person type.
    /// </summary>
    private static IEnumerable<PersonInfo> OrderLikeSourceDto(IReadOnlyList<PersonInfo> people)
    {
        return people
            .OrderBy(p => p.SortOrder ?? int.MaxValue)
            .ThenBy(p => p.Type switch
            {
                Jellyfin.Data.Enums.PersonKind.Actor => 0,
                Jellyfin.Data.Enums.PersonKind.GuestStar => 1,
                Jellyfin.Data.Enums.PersonKind.Director => 2,
                Jellyfin.Data.Enums.PersonKind.Writer => 3,
                Jellyfin.Data.Enums.PersonKind.Producer => 4,
                Jellyfin.Data.Enums.PersonKind.Composer => 4,
                _ => 10
            });
    }
}
