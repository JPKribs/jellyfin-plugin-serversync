using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Service for synchronizing the metadata sync table with source and local servers.
/// Uses file path matching (like History Sync) to correlate items.
/// </summary>
public class MetadataSyncTableService
{
    private readonly ILogger<MetadataSyncTableService> _logger;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataSyncTableService"/> class.
    /// </summary>
    public MetadataSyncTableService(
        ILogger<MetadataSyncTableService> logger,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Re-reads the local side of a record so the modal shows live data
    /// rather than the snapshot captured at the last metadata refresh. Call
    /// this from per-item endpoints — without it, a successful Sync apply
    /// updates local state in Jellyfin but the modal keeps showing the
    /// pre-sync local blob until the user re-runs the full Refresh task.
    /// Source-side fields (and all hashes) are left untouched, so this is
    /// a read-only refresh of the displayed Local columns.
    /// </summary>
    public void RefreshLocalSnapshot(
        MetadataSyncItem item,
        bool syncMetadata,
        bool syncImages,
        bool syncPeople,
        bool syncStudios,
        bool syncGenres,
        bool syncTags)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrEmpty(item.LocalItemId)) return;
        if (!Guid.TryParse(item.LocalItemId, out var localId)) return;

        var localItem = _libraryManager.GetItemById(localId);
        if (localItem == null) return;

        if (syncMetadata)
        {
            RebuildLocalMetadataBlob(item, localItem, syncGenres, syncTags);
        }

        if (syncImages)
        {
            RebuildLocalImagesBlob(item, localItem);
        }

        if (syncPeople)
        {
            RebuildLocalPeopleBlob(item, localItem);
        }

        if (syncStudios)
        {
            RebuildLocalStudiosBlob(item, localItem);
        }
    }

    /// <summary>
    /// Enriches the source-side image manifest in <paramref name="item"/>
    /// with Size / Width / Height fetched from the source server. The refresh
    /// task builds the source manifest from <c>BaseItemDto.ImageTags</c> for
    /// performance — tag-only with Size=0 — so the modal would otherwise
    /// render source-side as "0 B" while local has a real KB value. This
    /// per-modal-open helper compensates with one HTTP call.
    /// </summary>
    public async Task EnrichSourceImageSizesAsync(
        MetadataSyncItem item,
        SourceServerClient client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!Guid.TryParse(item.SourceItemId, out var sourceItemGuid)) return;

        item.Images.Source = await ImageManifestEnricher.EnrichAsync(
            item.Images.Source,
            sourceItemGuid,
            client,
            _logger,
            item.ItemName ?? item.SourceItemId,
            cancellationToken).ConfigureAwait(false);
    }

    private static void RebuildLocalMetadataBlob(MetadataSyncItem item, BaseItem localItem, bool syncGenres, bool syncTags)
    {
        var localVideo = localItem as MediaBrowser.Controller.Entities.Video;

        var localMetadata = new Dictionary<string, object?>
        {
            ["Name"] = localItem.Name,
            ["OriginalTitle"] = localItem.OriginalTitle,
            // SortName intentionally excluded from comparison: Jellyfin
            // normalizes/derives SortName from Name on its own (e.g. "the
            // X" → "x", drops articles/punctuation), so source.SortName
            // (whatever the source server's normalizer produced) and
            // local.SortName (whatever the local normalizer produced) will
            // never match for the same item, producing a permanent
            // false-positive diff. ForcedSortName is the user-controlled
            // override and IS still synced.
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

    private static void RebuildLocalImagesBlob(MetadataSyncItem item, BaseItem localItem)
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

    private void RebuildLocalPeopleBlob(MetadataSyncItem item, BaseItem localItem)
    {
        var localPeopleList = _libraryManager.GetPeople(localItem);
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

    private static void RebuildLocalStudiosBlob(MetadataSyncItem item, BaseItem localItem)
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

    /// <summary>
    /// Builds a path → <see cref="BaseItem"/> lookup for the given local
    /// library folder. Used by the refresh task to skip over source items
    /// that have no local correlate without paying for a heavy bulk fetch
    /// of source metadata first. Folders and leaves are returned in
    /// separate dictionaries so callers can match by item type.
    /// </summary>
    public (Dictionary<string, BaseItem> Leaves, Dictionary<string, BaseItem> Folders) GetLocalItemsByPath(Guid localLibraryId)
    {
        var leaves = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);

        if (_libraryManager.GetItemById(localLibraryId) is not Folder root)
        {
            return (leaves, folders);
        }

        foreach (var item in root.GetRecursiveChildren())
        {
            if (string.IsNullOrEmpty(item.Path))
            {
                continue;
            }

            if (item is Folder)
            {
                folders[item.Path] = item;
            }
            else
            {
                leaves[item.Path] = item;
            }
        }

        return (leaves, folders);
    }

    /// <summary>
    /// Builds a fully-populated <see cref="MetadataSyncItem"/> from a source
    /// item under the given library mapping. Returns null when the source
    /// path is missing, the library filter excludes the path, or the local
    /// item can't be matched by translated path.
    /// <para>
    /// The category flags gate which <see cref="SyncableValue{T}"/> blobs
    /// are populated; Genres and Tags within the metadata blob are further
    /// gated by <paramref name="syncGenres"/> / <paramref name="syncTags"/>
    /// so the hash short-circuit stays consistent with the apply path.
    /// </para>
    /// </summary>
    public async Task<MetadataSyncItem?> BuildRecordAsync(
        LibraryMapping libraryMapping,
        BaseItemDto sourceItem,
        bool isFolder,
        bool syncMetadata,
        bool syncImages,
        bool syncPeople,
        bool syncStudios,
        bool syncGenres,
        bool syncTags,
        SourceServerClient client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(libraryMapping);
        ArgumentNullException.ThrowIfNull(sourceItem);

        var sourcePath = sourceItem.Path;
        if (string.IsNullOrEmpty(sourcePath))
        {
            // Folder items occasionally lack a path (virtual Seasons,
            // some MusicArtists). They can't be path-matched locally.
            return null;
        }

        // Library-filter exclusion. Returning null causes the base's prune
        // pass to delete any pre-existing row for this key.
        if (libraryMapping.FilterMode != LibraryFilterMode.AllowAll
            && libraryMapping.FilteredItems?.Count > 0
            && PathUtilities.IsItemFiltered(sourcePath, libraryMapping.SourceRootPath, libraryMapping.FilterMode, libraryMapping.FilteredItems))
        {
            return null;
        }

        var sourceItemId = sourceItem.Id!.Value.ToString("N", CultureInfo.InvariantCulture);
        var localPath = PathUtilities.TranslatePath(sourcePath, libraryMapping.SourceRootPath, libraryMapping.LocalRootPath);

        var localItem = _libraryManager.FindByPath(localPath, isFolder: isFolder);
        if (localItem == null)
        {
            // No local match — skip. If a row existed previously the base
            // class will prune it.
            return null;
        }

        var localItemId = localItem.Id.ToString("N", CultureInfo.InvariantCulture);

        var item = new MetadataSyncItem
        {
            SourceLibraryId = libraryMapping.SourceLibraryId,
            LocalLibraryId = libraryMapping.LocalLibraryId ?? string.Empty,
            SourceItemId = sourceItemId,
            LocalItemId = localItemId,
            ItemName = sourceItem.Name ?? System.IO.Path.GetFileNameWithoutExtension(sourcePath),
            SourcePath = sourcePath,
            LocalPath = localPath,
            ItemType = sourceItem.Type?.ToString(),
            IsFolder = isFolder,
            StatusDate = DateTime.UtcNow
        };

        if (syncMetadata)
        {
            SetMetadataFieldValues(item, sourceItem, localItem, syncGenres, syncTags);
        }

        if (syncImages)
        {
            await SetImagesValuesAsync(item, sourceItem, localItem, client, cancellationToken).ConfigureAwait(false);
        }

        if (syncPeople)
        {
            SetPeopleValues(item, sourceItem, localItem);
        }

        if (syncStudios)
        {
            SetStudiosValues(item, sourceItem, localItem);
        }

        return item;
    }

    /// <summary>
    /// Sets metadata field values — simple text/number fields and arrays.
    /// Genres and Tags are gated on the corresponding config flags so the
    /// hash short-circuit stays honest: what you sync = what you compare =
    /// what you hash. Toggling <c>MetadataSyncGenres</c> changes the source
    /// blob (and its hash) on the next Refresh, which triggers a re-queue.
    /// </summary>
    private void SetMetadataFieldValues(
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
            // SortName intentionally excluded — see comment in the
            // local-side blob below. Servers normalize SortName from Name
            // independently, so cross-server comparison always diffs.
            // ForcedSortName (user override) is still synced.
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

        // Extract local metadata if item exists
        if (localItem != null)
        {
            // Try to cast to Video for video-specific properties
            var localVideo = localItem as MediaBrowser.Controller.Entities.Video;

            var localMetadata = new Dictionary<string, object?>
            {
                // Core info
                ["Name"] = localItem.Name,
                ["OriginalTitle"] = localItem.OriginalTitle,
                // SortName excluded — see source-side comment.
                ["ForcedSortName"] = localItem.ForcedSortName,
                ["Overview"] = localItem.Overview,
                ["Tagline"] = localItem.Tagline,

                // Ratings
                ["OfficialRating"] = localItem.OfficialRating,
                ["CustomRating"] = localItem.CustomRating,
                ["CommunityRating"] = localItem.CommunityRating,
                ["CriticRating"] = localItem.CriticRating,

                // Dates
                ["PremiereDate"] = localItem.PremiereDate,
                ["EndDate"] = localItem.EndDate,
                ["ProductionYear"] = localItem.ProductionYear,

                // External provider IDs (normalize: filter nulls and sort to match source format)
                ["ProviderIds"] = localItem.ProviderIds?
                    .Where(kvp => kvp.Value != null)
                    .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),

                // Series/Episode info
                ["IndexNumber"] = localItem.IndexNumber,
                ["ParentIndexNumber"] = localItem.ParentIndexNumber,

                // Language preferences
                ["PreferredMetadataCountryCode"] = localItem.PreferredMetadataCountryCode,
                ["PreferredMetadataLanguage"] = localItem.PreferredMetadataLanguage,

                // Display/format properties (Video-specific)
                ["AspectRatio"] = localVideo?.AspectRatio,
                ["Video3DFormat"] = localVideo?.Video3DFormat?.ToString(),

                // Lock settings (prevents metadata providers from overwriting)
                ["LockedFields"] = localItem.LockedFields?.Select(f => f.ToString())
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                ["LockData"] = localItem.IsLocked  // Lock this item to prevent future changes
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
    }

    /// <summary>
    /// Sets images values for the source side using image tags from the
    /// already-fetched <see cref="BaseItemDto"/>. The previous implementation
    /// made a per-item HTTP round-trip to <c>/Items/{id}/Images</c> just to
    /// get pixel dimensions and on-disk size — turning a metadata refresh on
    /// a 30-50k-item library into hours of serial round-trips. Source-side
    /// image change detection only needs a stable per-image fingerprint
    /// across refreshes; Jellyfin's image <c>Tag</c> is exactly that (it
    /// changes when image content changes), and is included in the bulk
    /// list response at zero extra cost. The
    /// <see cref="ImageManifestComparator"/> hash incorporates tags so the
    /// <c>SourceHash == SyncedHash</c> short-circuit still works.
    /// </summary>
    private async Task SetImagesValuesAsync(
        MetadataSyncItem item,
        BaseItemDto sourceItem,
        BaseItem? localItem,
        SourceServerClient client,
        CancellationToken cancellationToken)
    {
        var sourceImagesByType = new Dictionary<string, List<ImageInfoDto>>();
        PopulateSourceImagesFromTags(sourceItem, sourceImagesByType);

        string? sourceManifestJson = sourceImagesByType.Count > 0
            ? JsonSerializer.Serialize(sourceImagesByType)
            : null;

        // Enrich source-side image manifest with real Size/Width/Height via
        // /Items/{id}/Images. PopulateSourceImagesFromTags builds the source
        // side tag-only (Size=0) from BaseItemDto.ImageTags — fast but leaves
        // the comparator with no honest signal to compare against the sized
        // local manifest. Without enrichment here, the comparator's
        // tag-only-vs-sized fallback fires on every row, every refresh —
        // every row queues, every row re-applies, every refresh wastes the
        // bandwidth of redownloading images that already match. One per-item
        // HTTP at refresh time gives the comparator real numbers, so rows
        // that genuinely match Source==Local stay Synced and only true
        // divergences queue.
        if (sourceManifestJson != null
            && client != null
            && Guid.TryParse(item.SourceItemId, out var sourceItemGuid))
        {
            try
            {
                sourceManifestJson = await Utilities.ImageManifestEnricher.EnrichAsync(
                    sourceManifestJson,
                    sourceItemGuid,
                    client,
                    _logger,
                    item.ItemName ?? item.SourceItemId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Source image enrichment failed for {ItemName}; comparator will fall back to tag-only", item.ItemName);
            }
        }

        // UpdateSource recomputes the hash via ImageManifestComparator over
        // the (now enriched) manifest, so the SourceHash short-circuit
        // remains stable across refreshes.
        item.Images.UpdateSource(sourceManifestJson);

        // Local images with size/dimensions per type
        if (localItem != null)
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
                if (images.Count > 0)
                {
                    var imageInfoList = new List<ImageInfoDto>();
                    for (int idx = 0; idx < images.Count; idx++)
                    {
                        var img = images[idx];
                        long fileSize = 0;

                        // Try to get actual file size from disk
                        if (!string.IsNullOrEmpty(img.Path) && System.IO.File.Exists(img.Path))
                        {
                            try
                            {
                                var fileInfo = new System.IO.FileInfo(img.Path);
                                fileSize = fileInfo.Length;
                            }
                            catch (System.IO.IOException)
                            {
                                // Ignore file access errors - file size will remain 0
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
            }

            item.Images.Local = localImagesByType.Count > 0
                ? JsonSerializer.Serialize(localImagesByType)
                : null;
        }
    }

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
    /// Sets people values (actors, directors, writers).
    /// Compares by Name, Role, and Type (not GUID) to allow syncing between servers.
    /// </summary>
    private void SetPeopleValues(MetadataSyncItem item, BaseItemDto sourceItem, BaseItem? localItem)
    {
        // Serialize people from source (using Name, Role, Type - not GUID)
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

            // Sort for consistent comparison (order shouldn't matter for people)
            sourcePeople.Sort(ComparePeopleDicts);

            item.People.UpdateSource(JsonSerializer.Serialize(sourcePeople));
        }
        else
        {
            item.People.UpdateSource("[]");
        }

        // Extract local people for comparison (using Name, Role, Type - not GUID)
        if (localItem != null)
        {
            var localPeopleList = _libraryManager.GetPeople(localItem);
            if (localPeopleList != null && localPeopleList.Count > 0)
            {
                var localPeople = new List<Dictionary<string, string>>();
                foreach (var person in localPeopleList)
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

                    if (person.Type != default)
                    {
                        personDict["Type"] = person.Type.ToString();
                    }

                    if (personDict.ContainsKey("Name"))
                    {
                        localPeople.Add(personDict);
                    }
                }

                // Sort for consistent comparison (order shouldn't matter for people)
                localPeople.Sort(ComparePeopleDicts);

                item.People.Local = JsonSerializer.Serialize(localPeople);
            }
            else
            {
                item.People.Local = "[]";
            }
        }
        else
        {
            item.People.Local = null;
        }
    }

    /// <summary>
    /// Sets studios values.
    /// Compares by studio name only to allow syncing between servers.
    /// </summary>
    private void SetStudiosValues(MetadataSyncItem item, BaseItemDto sourceItem, BaseItem? localItem)
    {
        // Serialize studios from source (just studio names).
        // Filter null/whitespace names symmetrically with the local-side
        // builder below — without this, a source studio with an empty name
        // string round-trips into the source blob but the local-side blob
        // (which filters whitespace) would not include it, producing a
        // permanent false-positive diff for that item.
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

        // Extract local studios for comparison (just studio names)
        if (localItem != null)
        {
            if (localItem.Studios != null && localItem.Studios.Length > 0)
            {
                // Filter out empty/whitespace names and sort for consistent comparison
                var validStudios = localItem.Studios
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                item.Studios.Local = validStudios.Count > 0
                    ? JsonSerializer.Serialize(validStudios)
                    : "[]";
            }
            else
            {
                item.Studios.Local = "[]";
            }
        }
        else
        {
            item.Studios.Local = null;
        }
    }

    /// <summary>
    /// Compares two people dictionaries by Name, then Role, then Type for consistent sorting.
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
