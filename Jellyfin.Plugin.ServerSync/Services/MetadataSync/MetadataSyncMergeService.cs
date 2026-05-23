using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Per-category merge logic for Metadata Sync. Each method extracts the
/// comparable representation of one category (Metadata / Images / People /
/// Studios) and writes it onto the <see cref="MetadataSyncItem"/>; source-wins
/// per category. Static class — cross-cutting dependencies are passed in.
/// </summary>
public static class MetadataSyncMergeService
{
    // ===================================================================
    // Per-category source + local blob building (called from BuildRecordAsync)
    // ===================================================================

    /// <summary>
    /// Builds the source-side and local-side metadata blobs (text/number
    /// fields, provider IDs, locked fields, etc.) on <paramref name="item"/>.
    /// Genres and Tags are gated on the corresponding config flags so the
    /// hash short-circuit stays honest: what you sync = what you compare =
    /// what you hash.
    /// </summary>
    public static void MergeMetadataFields(
        MetadataSyncItem item,
        BaseItemDto sourceItem,
        BaseItem? localItem,
        bool syncGenres,
        bool syncTags)
    {
        // Extract provider IDs as a simple dictionary (external IDs like IMDB, TMDB).
        // Sort by key to ensure consistent ordering for comparison.
        // UnwrapKiotaPrimitive is required: AdditionalData values are Kiota
        // UntypedNode wrappers, and a naive .ToString() yields the type name
        // instead of the wrapped value, producing permanent IMDB/TMDB desyncs.
        var sourceProviderIds = sourceItem.ProviderIds?.AdditionalData?
            .Select(kvp => (kvp.Key, Value: MediaItemUtilities.UnwrapKiotaPrimitive(kvp.Value)))
            .Where(kvp => !string.IsNullOrEmpty(kvp.Value))
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        // Extract only simple metadata fields that can be directly copied.
        // Excluded fields that can't be synced:
        //   - DateCreated: Read-only, set when item first added to database
        var sourceMetadata = new Dictionary<string, object?>
        {
            // Core info
            ["Name"] = sourceItem.Name,
            ["OriginalTitle"] = sourceItem.OriginalTitle,
            // SortName intentionally excluded — servers normalize SortName
            // from Name independently, so cross-server comparison always
            // diffs. ForcedSortName (user override) is still synced.
            ["ForcedSortName"] = sourceItem.ForcedSortName,
            ["Overview"] = sourceItem.Overview,

            // Tagline - source has array but we take first one (local is singular)
            ["Tagline"] = sourceItem.Taglines?.FirstOrDefault(),

            // Ratings
            ["OfficialRating"] = sourceItem.OfficialRating,
            ["CustomRating"] = sourceItem.CustomRating,
            ["CommunityRating"] = sourceItem.CommunityRating,
            ["CriticRating"] = sourceItem.CriticRating,

            // Dates
            ["PremiereDate"] = sourceItem.PremiereDate,
            ["EndDate"] = sourceItem.EndDate,
            ["ProductionYear"] = sourceItem.ProductionYear,

            // External provider IDs
            ["ProviderIds"] = sourceProviderIds,

            // Series/Episode info
            ["IndexNumber"] = sourceItem.IndexNumber,
            ["ParentIndexNumber"] = sourceItem.ParentIndexNumber,

            // Language preferences
            ["PreferredMetadataCountryCode"] = sourceItem.PreferredMetadataCountryCode,
            ["PreferredMetadataLanguage"] = sourceItem.PreferredMetadataLanguage,

            // Display/format properties
            ["AspectRatio"] = sourceItem.AspectRatio,
            ["Video3DFormat"] = sourceItem.Video3DFormat?.ToString(),

            // Lock settings (prevents metadata providers from overwriting).
            // Sort LockedFields so source/local serialize identically — the
            // JSON comparator compares arrays element-by-element, so an
            // unsorted source vs an alphabetised local would diff every refresh.
            ["LockedFields"] = sourceItem.LockedFields?.Select(f => f.ToString())
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ["LockData"] = sourceItem.LockData ?? false  // IsLocked - lock this item to prevent future changes
        };

        if (syncGenres)
        {
            // Normalize collapses null / [] / [""] into a single canonical
            // null on both sides so Jellyfin's storage normalization (which
            // can return [""] for what we wrote as []) doesn't produce a
            // permanent verification mismatch.
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
    /// Builds the source-side and local-side image manifest blobs. Source-side
    /// is built from <c>BaseItemDto</c> image tags (free in the bulk list
    /// response), then enriched with real Size/Width/Height via a per-item
    /// HTTP call so the <see cref="ImageManifestComparator"/> has honest
    /// numbers to compare against the sized local manifest. Without enrichment
    /// the tag-only-vs-sized fallback fires on every row and every refresh
    /// pointlessly re-queues already-synced images.
    /// </summary>
    public static async Task MergeImagesAsync(
        MetadataSyncItem item,
        BaseItemDto sourceItem,
        BaseItem? localItem,
        SourceServerClient? client,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var sourceImagesByType = new Dictionary<string, List<ImageInfoDto>>();
        PopulateSourceImagesFromTags(sourceItem, sourceImagesByType);

        string? sourceManifestJson = sourceImagesByType.Count > 0
            ? JsonSerializer.Serialize(sourceImagesByType)
            : null;

        if (sourceManifestJson != null
            && client != null
            && Guid.TryParse(item.SourceItemId, out var sourceItemGuid))
        {
            try
            {
                sourceManifestJson = await ImageManifestEnricher.EnrichAsync(
                    sourceManifestJson,
                    sourceItemGuid,
                    client,
                    logger,
                    item.ItemName ?? item.SourceItemId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Source image enrichment failed for {ItemName}; comparator will fall back to tag-only", item.ItemName);
            }
        }

        // UpdateSource recomputes the hash via ImageManifestComparator over
        // the (now enriched) manifest, so the SourceHash short-circuit
        // remains stable across refreshes.
        item.Images.UpdateSource(sourceManifestJson);

        if (localItem != null)
        {
            RebuildLocalImagesBlob(item, localItem);
        }
    }

    /// <summary>
    /// Builds the source-side and local-side people blobs (actors, directors,
    /// writers). Compares by Name + Role + Type rather than GUID so syncing
    /// across servers with different person IDs still matches.
    /// </summary>
    public static void MergePeople(
        MetadataSyncItem item,
        BaseItemDto sourceItem,
        BaseItem? localItem,
        ILibraryManager libraryManager)
    {
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

            sourcePeople.Sort(ComparePeopleDicts);
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
    /// Builds the source-side and local-side studios blobs. Compares by
    /// studio name only so syncing across servers with different studio IDs
    /// still matches. Both sides filter null/whitespace symmetrically to
    /// avoid permanent false-positive diffs.
    /// </summary>
    public static void MergeStudios(
        MetadataSyncItem item,
        BaseItemDto sourceItem,
        BaseItem? localItem)
    {
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

    // ===================================================================
    // Local-side rebuild (used by RefreshLocalSnapshot for per-modal refresh)
    // ===================================================================

    /// <summary>
    /// Rebuilds the local-side metadata blob on <paramref name="item"/> from
    /// the live <paramref name="localItem"/>. Used by RefreshLocalSnapshot to
    /// show fresh local state in the modal after a sync apply without re-
    /// running the full Refresh task.
    /// </summary>
    public static void RebuildLocalMetadataBlob(MetadataSyncItem item, BaseItem localItem, bool syncGenres, bool syncTags)
    {
        var localVideo = localItem as MediaBrowser.Controller.Entities.Video;

        var localMetadata = new Dictionary<string, object?>
        {
            ["Name"] = localItem.Name,
            ["OriginalTitle"] = localItem.OriginalTitle,
            // SortName excluded — see source-side comment in MergeMetadataFields.
            ["ForcedSortName"] = localItem.ForcedSortName,
            ["Overview"] = localItem.Overview,
            ["Tagline"] = localItem.Tagline,
            ["OfficialRating"] = localItem.OfficialRating,
            ["CustomRating"] = localItem.CustomRating,
            ["CommunityRating"] = localItem.CommunityRating,
            ["CriticRating"] = localItem.CriticRating,
            ["PremiereDate"] = localItem.PremiereDate,
            ["EndDate"] = localItem.EndDate,
            ["ProductionYear"] = localItem.ProductionYear,
            ["ProviderIds"] = localItem.ProviderIds?
                .Where(kvp => kvp.Value != null)
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
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
    /// Rebuilds the local-side image manifest blob on <paramref name="item"/>
    /// from on-disk images for <paramref name="localItem"/>.
    /// </summary>
    public static void RebuildLocalImagesBlob(MetadataSyncItem item, BaseItem localItem)
    {
        var localImagesByType = new Dictionary<string, List<ImageInfoDto>>();
        var imageTypes = new[]
        {
            MediaBrowser.Model.Entities.ImageType.Primary,
            MediaBrowser.Model.Entities.ImageType.Backdrop,
            MediaBrowser.Model.Entities.ImageType.Logo,
            MediaBrowser.Model.Entities.ImageType.Thumb,
            MediaBrowser.Model.Entities.ImageType.Banner,
            MediaBrowser.Model.Entities.ImageType.Art,
            MediaBrowser.Model.Entities.ImageType.Disc
        };

        foreach (var imageType in imageTypes)
        {
            var images = localItem.GetImages(imageType).ToList();
            if (images.Count == 0) continue;

            var imageInfoList = new List<ImageInfoDto>();
            for (int idx = 0; idx < images.Count; idx++)
            {
                var img = images[idx];
                long fileSize = 0;
                if (!string.IsNullOrEmpty(img.Path) && System.IO.File.Exists(img.Path))
                {
                    try
                    {
                        fileSize = new System.IO.FileInfo(img.Path).Length;
                    }
                    catch (System.IO.IOException)
                    {
                        // Ignore — leave size at 0
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

        item.Images.Local = localImagesByType.Count > 0
            ? JsonSerializer.Serialize(localImagesByType)
            : null;
    }

    /// <summary>
    /// Rebuilds the local-side people blob on <paramref name="item"/>.
    /// </summary>
    public static void RebuildLocalPeopleBlob(MetadataSyncItem item, BaseItem localItem, ILibraryManager libraryManager)
    {
        var localPeopleList = libraryManager.GetPeople(localItem);
        if (localPeopleList == null || localPeopleList.Count == 0)
        {
            item.People.Local = "[]";
            return;
        }

        var localPeople = new List<Dictionary<string, string>>();
        foreach (var person in localPeopleList)
        {
            if (string.IsNullOrEmpty(person.Name)) continue;

            var personDict = new Dictionary<string, string> { ["Name"] = person.Name };
            if (!string.IsNullOrEmpty(person.Role)) personDict["Role"] = person.Role;
            personDict["Type"] = person.Type.ToString();
            localPeople.Add(personDict);
        }

        localPeople.Sort(ComparePeopleDicts);
        item.People.Local = JsonSerializer.Serialize(localPeople);
    }

    /// <summary>
    /// Rebuilds the local-side studios blob on <paramref name="item"/>.
    /// </summary>
    public static void RebuildLocalStudiosBlob(MetadataSyncItem item, BaseItem localItem)
    {
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

    // ===================================================================
    // Parity helpers (match HistorySyncMergeService surface)
    // ===================================================================

    /// <summary>
    /// True if any enabled category on the record has changes worth syncing.
    /// Mirrors <see cref="HistorySyncMergeService.HasChangesToSync"/>.
    /// </summary>
    public static bool HasChangesToSync(MetadataSyncItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.HasChanges;
    }

    /// <summary>
    /// Human-readable summary of which categories changed, for logs / modals.
    /// Mirrors <see cref="HistorySyncMergeService.GetChangeSummary"/>.
    /// </summary>
    public static string GetChangeSummary(MetadataSyncItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var changes = new List<string>();
        if (item.HasMetadataChanges) changes.Add("Metadata");
        if (item.HasImagesChanges) changes.Add("Images");
        if (item.HasPeopleChanges) changes.Add("People");
        if (item.HasStudiosChanges) changes.Add("Studios");

        return changes.Count > 0 ? string.Join(", ", changes) : "No changes";
    }

    // ===================================================================
    // Private helpers
    // ===================================================================

    /// <summary>
    /// Populates the source-side image manifest from <see cref="BaseItemDto"/>
    /// tags. Tags are returned in the bulk list response for free; using them
    /// avoids the per-item HTTP call to <c>/Items/{id}/Images</c>.
    /// </summary>
    private static void PopulateSourceImagesFromTags(BaseItemDto sourceItem, Dictionary<string, List<ImageInfoDto>> sourceImagesByType)
    {
        // Process single image types from ImageTags. The values are Kiota
        // UntypedString wrappers — naive .ToString() returns the type name,
        // not the tag. Unwrap properly so per-item Tag is the real hash.
        if (sourceItem.ImageTags?.AdditionalData != null)
        {
            foreach (var kvp in sourceItem.ImageTags.AdditionalData)
            {
                // Skip image types that this sync path can't apply to its
                // targets. Metadata sync only operates on Movie/Series/Season/
                // Episode/Album/Artist/BoxSet — never Person — so a Profile
                // tag in the source DTO (sometimes present on TMDB-imported
                // metadata for actors associated with a Movie record) cannot
                // be saved on the local item; Jellyfin's repository drops
                // such writes silently. Including them here would put the
                // Profile entry in the source manifest, fail the post-apply
                // verification ("Profile present on source but missing on
                // local"), and pin the row in Errored forever.
                if (string.Equals(kvp.Key, "Profile", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var tag = MediaItemUtilities.UnwrapKiotaPrimitive(kvp.Value);
                if (!string.IsNullOrEmpty(tag))
                {
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
        }

        // Process multiple backdrops
        if (sourceItem.BackdropImageTags?.Count > 0)
        {
            sourceImagesByType["Backdrop"] = sourceItem.BackdropImageTags
                .Select((tag, idx) => new ImageInfoDto { ImageType = "Backdrop", ImageIndex = idx, Tag = tag })
                .ToList();
        }
    }

    /// <summary>
    /// Compares two people dictionaries by Name, then Role, then Type for
    /// consistent sorting across servers.
    /// </summary>
    private static int ComparePeopleDicts(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        a.TryGetValue("Name", out var nameA);
        b.TryGetValue("Name", out var nameB);
        var nameCompare = string.Compare(nameA, nameB, StringComparison.OrdinalIgnoreCase);
        if (nameCompare != 0)
        {
            return nameCompare;
        }

        a.TryGetValue("Role", out var roleA);
        b.TryGetValue("Role", out var roleB);
        var roleCompare = string.Compare(roleA, roleB, StringComparison.OrdinalIgnoreCase);
        if (roleCompare != 0)
        {
            return roleCompare;
        }

        a.TryGetValue("Type", out var typeA);
        b.TryGetValue("Type", out var typeB);
        return string.Compare(typeA, typeB, StringComparison.OrdinalIgnoreCase);
    }
}
