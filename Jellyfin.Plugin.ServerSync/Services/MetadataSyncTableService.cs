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
using Jellyfin.Plugin.ServerSync.Models.MetadataSync.Configuration;
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
    /// Processes a library mapping, fetching metadata and updating the sync database.
    /// </summary>
    /// <param name="client">Source server client.</param>
    /// <param name="database">Sync database.</param>
    /// <param name="libraryMapping">Library mapping.</param>
    /// <param name="syncMetadata">Whether to sync metadata fields.</param>
    /// <param name="syncImages">Whether to sync images.</param>
    /// <param name="syncPeople">Whether to sync people.</param>
    /// <param name="syncStudios">Whether to sync studios.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="onItemProcessed">Optional callback after each item is processed.</param>
    /// <returns>Number of items processed.</returns>
    public async Task<int> ProcessLibraryAsync(
        SourceServerClient client,
        SyncDatabase database,
        LibraryMapping libraryMapping,
        bool syncMetadata,
        bool syncImages,
        bool syncPeople,
        bool syncStudios,
        MetadataRefreshMode refreshMode,
        CancellationToken cancellationToken,
        Action? onItemProcessed = null,
        bool syncFolderItems = false)
    {
        var sourceLibraryId = Guid.Parse(libraryMapping.SourceLibraryId);

        var processedItems = 0;

        // Load existing metadata items for this library (one record per item now)
        var existingItems = new Dictionary<string, MetadataSyncItem>();
        try
        {
            var items = database.GetMetadataSyncItemsByLibrary(libraryMapping.SourceLibraryId);
            foreach (var item in items)
            {
                existingItems[item.SourceItemId] = item;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load existing metadata items for library {Library}",
                libraryMapping.SourceLibraryName);
            return 0;
        }

        // Remove existing metadata records for items matching the library filter
        if (libraryMapping.FilterMode != LibraryFilterMode.AllowAll && libraryMapping.FilteredItems?.Count > 0)
        {
            var keysToRemove = new List<string>();
            foreach (var kvp in existingItems)
            {
                if (!string.IsNullOrEmpty(kvp.Value.SourcePath)
                    && PathUtilities.IsItemFiltered(kvp.Value.SourcePath, libraryMapping.SourceRootPath, libraryMapping.FilterMode, libraryMapping.FilteredItems))
                {
                    database.DeleteMetadataSyncItemsBySourceItem(kvp.Key);
                    keysToRemove.Add(kvp.Key);
                    _logger.LogInformation("Removed metadata record for {Path} (filtered by library filter)", kvp.Value.SourcePath);
                }
            }

            foreach (var key in keysToRemove)
            {
                existingItems.Remove(key);
            }
        }

        _logger.LogInformation(
            "Processing metadata for library {Library} (Metadata: {Metadata}, Images: {Images}, People: {People}, Studios: {Studios}, Mode: {Mode})",
            libraryMapping.SourceLibraryName, syncMetadata, syncImages, syncPeople, syncStudios, refreshMode);

        // Track which source items are still present on the source server
        var seenSourceItemIds = new HashSet<string>();

        processedItems = await PaginatedFetchUtility.FetchAllPagesAsync(
            fetchPage: (startIndex, batchSize, ct) => client.GetLibraryItemsWithMetadataAsync(sourceLibraryId, startIndex, batchSize, ct),
            processItem: async (sourceItem, ct) =>
            {
                var sid = sourceItem.Id!.Value.ToString("N", CultureInfo.InvariantCulture);
                seenSourceItemIds.Add(sid);

                await ProcessMetadataItemAsync(
                    client,
                    database,
                    libraryMapping,
                    sourceItem,
                    syncMetadata,
                    syncImages,
                    syncPeople,
                    syncStudios,
                    refreshMode,
                    existingItems,
                    ct).ConfigureAwait(false);
                return true;
            },
            libraryName: libraryMapping.SourceLibraryName,
            sourceRootPath: libraryMapping.SourceRootPath,
            filterMode: libraryMapping.FilterMode,
            filteredItems: libraryMapping.FilteredItems,
            logger: _logger,
            cancellationToken: cancellationToken,
            onItemProcessed: onItemProcessed).ConfigureAwait(false);

        // Process folder-type items (Series, Season, MusicAlbum, MusicArtist, BoxSet)
        if (syncFolderItems)
        {
            _logger.LogInformation("Processing folder items for library {Library}", libraryMapping.SourceLibraryName);

            var folderItemsProcessed = await PaginatedFetchUtility.FetchAllPagesAsync(
                fetchPage: (startIndex, batchSize, ct) => client.GetLibraryFolderItemsWithMetadataAsync(sourceLibraryId, startIndex, batchSize, ct),
                processItem: async (sourceItem, ct) =>
                {
                    var sid = sourceItem.Id!.Value.ToString("N", CultureInfo.InvariantCulture);
                    seenSourceItemIds.Add(sid);

                    await ProcessMetadataItemAsync(
                        client,
                        database,
                        libraryMapping,
                        sourceItem,
                        syncMetadata,
                        syncImages,
                        syncPeople,
                        syncStudios,
                        refreshMode,
                        existingItems,
                        ct,
                        isFolder: true).ConfigureAwait(false);
                    return true;
                },
                libraryName: libraryMapping.SourceLibraryName,
                sourceRootPath: libraryMapping.SourceRootPath,
                filterMode: libraryMapping.FilterMode,
                filteredItems: libraryMapping.FilteredItems,
                logger: _logger,
                cancellationToken: cancellationToken,
                onItemProcessed: onItemProcessed).ConfigureAwait(false);

            processedItems += folderItemsProcessed;

            _logger.LogInformation(
                "Processed {Count} folder metadata items for library {Library}",
                folderItemsProcessed, libraryMapping.SourceLibraryName);
        }

        // Remove metadata records for items no longer present on the source server
        var staleCount = 0;
        foreach (var kvp in existingItems)
        {
            if (!seenSourceItemIds.Contains(kvp.Key))
            {
                database.DeleteMetadataSyncItemsBySourceItem(kvp.Key);
                staleCount++;
                _logger.LogDebug("Removed stale metadata record for {ItemName} (no longer on source)", kvp.Value.ItemName);
            }
        }

        if (staleCount > 0)
        {
            _logger.LogInformation("Removed {Count} stale metadata records from library {Library}", staleCount, libraryMapping.SourceLibraryName);
        }

        _logger.LogInformation(
            "Processed {Count} metadata items for library {Library}",
            processedItems, libraryMapping.SourceLibraryName);

        return processedItems;
    }

    /// <summary>
    /// Processes a single metadata item from the source server.
    /// </summary>
    private async Task ProcessMetadataItemAsync(
        SourceServerClient client,
        SyncDatabase database,
        LibraryMapping libraryMapping,
        BaseItemDto sourceItem,
        bool syncMetadata,
        bool syncImages,
        bool syncPeople,
        bool syncStudios,
        MetadataRefreshMode refreshMode,
        Dictionary<string, MetadataSyncItem> existingItems,
        CancellationToken cancellationToken,
        bool isFolder = false)
    {
        var sourceItemId = sourceItem.Id!.Value.ToString("N", CultureInfo.InvariantCulture);
        var sourcePath = sourceItem.Path;

        // Folder items may not always have a path (e.g., virtual Seasons, some MusicArtists)
        if (string.IsNullOrEmpty(sourcePath))
        {
            _logger.LogDebug("Skipping {ItemType} '{Name}' — no path available for matching", sourceItem.Type, sourceItem.Name);
            return;
        }

        // Translate path to local path
        var localPath = PathUtilities.TranslatePath(sourcePath, libraryMapping.SourceRootPath, libraryMapping.LocalRootPath);

        // Try to find the local item by path
        var localItem = _libraryManager.FindByPath(localPath, isFolder: isFolder);
        string? localItemId = localItem?.Id.ToString("N", CultureInfo.InvariantCulture);

        // Get existing item for this source item
        var existingItem = existingItems.GetValueOrDefault(sourceItemId);

        // Skip items without local match - only sync items that exist on both servers
        if (localItem == null)
        {
            // If we had a record for this item before but local item is gone, remove it
            if (existingItem != null)
            {
                database.DeleteMetadataSyncItemsBySourceItem(sourceItemId);
            }

            return;
        }

        if (existingItem != null)
        {
            // In SkipUnchanged mode, skip items whose ETag hasn't changed
            if (refreshMode == MetadataRefreshMode.SkipUnchanged
                && !string.IsNullOrEmpty(existingItem.SourceETag)
                && string.Equals(existingItem.SourceETag, sourceItem.Etag, StringComparison.Ordinal))
            {
                // ETag matches stored value - item unchanged, skip entirely
                return;
            }

            // Update existing item with all category values
            await UpdateMetadataItemAsync(existingItem, sourceItem, localItem, localPath, localItemId, syncMetadata, syncImages, syncPeople, syncStudios, client, cancellationToken).ConfigureAwait(false);
            existingItem.SourceETag = sourceItem.Etag;
            existingItem.ItemType = sourceItem.Type?.ToString();
            existingItem.IsFolder = isFolder;
            database.UpsertMetadataSyncItem(existingItem);
        }
        else
        {
            // Create new item with all category values (always process fully regardless of mode)
            var newItem = await CreateMetadataItemAsync(
                libraryMapping,
                sourceItem,
                sourceItemId,
                sourcePath,
                localPath,
                localItemId,
                localItem,
                syncMetadata,
                syncImages,
                syncPeople,
                syncStudios,
                client,
                cancellationToken).ConfigureAwait(false);

            newItem.SourceETag = sourceItem.Etag;
            newItem.ItemType = sourceItem.Type?.ToString();
            newItem.IsFolder = isFolder;
            database.UpsertMetadataSyncItem(newItem);
        }
    }

    /// <summary>
    /// Creates a new metadata sync item with all category values.
    /// </summary>
    private async Task<MetadataSyncItem> CreateMetadataItemAsync(
        LibraryMapping libraryMapping,
        BaseItemDto sourceItem,
        string sourceItemId,
        string sourcePath,
        string localPath,
        string? localItemId,
        BaseItem? localItem,
        bool syncMetadata,
        bool syncImages,
        bool syncPeople,
        bool syncStudios,
        SourceServerClient client,
        CancellationToken cancellationToken)
    {
        var item = new MetadataSyncItem
        {
            // Library context
            SourceLibraryId = libraryMapping.SourceLibraryId,
            LocalLibraryId = libraryMapping.LocalLibraryId ?? string.Empty,

            // Item identification
            SourceItemId = sourceItemId,
            LocalItemId = localItemId,
            ItemName = sourceItem.Name ?? System.IO.Path.GetFileNameWithoutExtension(sourcePath),
            SourcePath = sourcePath,
            LocalPath = localPath,

            // Tracking
            Status = BaseSyncStatus.Queued,
            StatusDate = DateTime.UtcNow
        };

        // Set values for each enabled category
        if (syncMetadata)
        {
            SetMetadataFieldValues(item, sourceItem, localItem);
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

        // Determine initial status based on whether there are changes
        if (string.IsNullOrEmpty(localItemId))
        {
            // Local item not found - can't sync yet
            item.Status = BaseSyncStatus.Queued;
        }
        else if (item.HasChanges)
        {
            // Has changes - queue for sync
            item.Status = BaseSyncStatus.Queued;
        }
        else
        {
            // No changes needed - already in sync
            item.Status = BaseSyncStatus.Synced;
            item.LastSyncTime = DateTime.UtcNow;
        }

        return item;
    }

    /// <summary>
    /// Updates an existing metadata sync item with current data.
    /// </summary>
    private async Task UpdateMetadataItemAsync(
        MetadataSyncItem item,
        BaseItemDto sourceItem,
        BaseItem? localItem,
        string localPath,
        string? localItemId,
        bool syncMetadata,
        bool syncImages,
        bool syncPeople,
        bool syncStudios,
        SourceServerClient client,
        CancellationToken cancellationToken)
    {
        // Update identification
        item.LocalItemId = localItemId;
        item.LocalPath = localPath;
        item.ItemName = sourceItem.Name ?? item.ItemName;

        // Update values for each enabled category
        if (syncMetadata)
        {
            SetMetadataFieldValues(item, sourceItem, localItem);
        }
        else
        {
            // Clear metadata if disabled
            item.SourceMetadataValue = null;
            item.LocalMetadataValue = null;
        }

        if (syncImages)
        {
            await SetImagesValuesAsync(item, sourceItem, localItem, client, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Clear images if disabled
            item.SourceImagesValue = null;
            item.LocalImagesValue = null;
            item.SourceImagesHash = null;
        }

        if (syncPeople)
        {
            SetPeopleValues(item, sourceItem, localItem);
        }
        else
        {
            // Clear people if disabled
            item.SourcePeopleValue = null;
            item.LocalPeopleValue = null;
        }

        if (syncStudios)
        {
            SetStudiosValues(item, sourceItem, localItem);
        }
        else
        {
            // Clear studios if disabled
            item.SourceStudiosValue = null;
            item.LocalStudiosValue = null;
        }

        // Preserve Ignored status - don't change status for ignored items
        if (item.Status == BaseSyncStatus.Ignored)
        {
            return;
        }

        // Update status if it was already synced but now has new changes
        if (item.Status == BaseSyncStatus.Synced && item.HasChanges)
        {
            item.Status = BaseSyncStatus.Queued;
            item.StatusDate = DateTime.UtcNow;
        }
        else if (item.Status == BaseSyncStatus.Queued && !item.HasChanges && !string.IsNullOrEmpty(localItemId))
        {
            // Previously queued but now in sync
            item.Status = BaseSyncStatus.Synced;
            item.StatusDate = DateTime.UtcNow;
            item.LastSyncTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Sets metadata field values - simple text/number fields and arrays.
    /// </summary>
    private void SetMetadataFieldValues(MetadataSyncItem item, BaseItemDto sourceItem, BaseItem? localItem)
    {
        // Extract provider IDs as a simple dictionary (external IDs like IMDB, TMDB)
        // Sort by key to ensure consistent ordering for comparison
        var sourceProviderIds = sourceItem.ProviderIds?.AdditionalData?
            .Where(kvp => kvp.Value != null)
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString());

        // Extract only simple metadata fields that can be directly copied
        // NOTE: Excluded fields that can't be synced:
        //   - DateCreated: Read-only, set when item first added to database
        var sourceMetadata = new Dictionary<string, object?>
        {
            // Core info
            ["Name"] = sourceItem.Name,
            ["OriginalTitle"] = sourceItem.OriginalTitle,
            ["SortName"] = sourceItem.SortName,
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

            // Genres
            ["Genres"] = sourceItem.Genres,

            // Tags
            ["Tags"] = sourceItem.Tags,

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

            // Lock settings (prevents metadata providers from overwriting)
            ["LockedFields"] = sourceItem.LockedFields?.Select(f => f.ToString()).ToArray(),
            ["LockData"] = sourceItem.LockData  // IsLocked - lock this item to prevent future changes
        };

        item.SourceMetadataValue = JsonSerializer.Serialize(sourceMetadata);

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
                ["SortName"] = localItem.SortName,
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

                // Genres
                ["Genres"] = localItem.Genres,

                // Tags
                ["Tags"] = localItem.Tags,

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
                ["LockedFields"] = localItem.LockedFields?.Select(f => f.ToString()).ToArray(),
                ["LockData"] = localItem.IsLocked  // Lock this item to prevent future changes
            };

            item.LocalMetadataValue = JsonSerializer.Serialize(localMetadata);
        }
    }

    /// <summary>
    /// Sets images values with per-type comparison, fetching actual image info from source server.
    /// </summary>
    private async Task SetImagesValuesAsync(
        MetadataSyncItem item,
        BaseItemDto sourceItem,
        BaseItem? localItem,
        SourceServerClient client,
        CancellationToken cancellationToken)
    {
        var sourceImagesByType = new Dictionary<string, List<ImageInfoDto>>();

        // Fetch actual image info from source server (includes size, width, height)
        if (sourceItem.Id.HasValue)
        {
            try
            {
                var imageInfoList = await client.GetItemImageInfoAsync(sourceItem.Id.Value, cancellationToken).ConfigureAwait(false);
                if (imageInfoList != null && imageInfoList.Count > 0)
                {
                    foreach (var img in imageInfoList)
                    {
                        var imageTypeName = img.ImageType?.ToString() ?? "Unknown";
                        if (!sourceImagesByType.TryGetValue(imageTypeName, out var imageList))
                        {
                            imageList = new List<ImageInfoDto>();
                            sourceImagesByType[imageTypeName] = imageList;
                        }

                        imageList.Add(new ImageInfoDto
                        {
                            ImageType = imageTypeName,
                            ImageIndex = img.ImageIndex ?? 0,
                            Size = img.Size ?? 0,
                            Width = img.Width ?? 0,
                            Height = img.Height ?? 0,
                            Tag = img.ImageTag
                        });
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch image info for item {ItemId}, falling back to tags", sourceItem.Id);

                // Fallback to image tags if API call fails
                PopulateSourceImagesFromTags(sourceItem, sourceImagesByType);
            }
        }
        else
        {
            // Fallback to image tags
            PopulateSourceImagesFromTags(sourceItem, sourceImagesByType);
        }

        // Only store if there are actual images
        if (sourceImagesByType.Count > 0)
        {
            item.SourceImagesValue = JsonSerializer.Serialize(sourceImagesByType);

            // Compute hash for change detection based on size and dimensions
            var hashInput = string.Join(";", sourceImagesByType
                .OrderBy(k => k.Key)
                .Select(k => $"{k.Key}:{string.Join(",", k.Value.Select(v => $"{v.Size}_{v.Width}x{v.Height}"))}"));
            var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hashInput));
            item.SourceImagesHash = Convert.ToHexString(hashBytes).ToLowerInvariant()[..32];
        }
        else
        {
            item.SourceImagesValue = null;
            item.SourceImagesHash = null;
        }

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

            item.LocalImagesValue = localImagesByType.Count > 0
                ? JsonSerializer.Serialize(localImagesByType)
                : null;
        }
    }

    /// <summary>
    /// Populates source images from image tags (fallback when API call fails).
    /// </summary>
    private void PopulateSourceImagesFromTags(BaseItemDto sourceItem, Dictionary<string, List<ImageInfoDto>> sourceImagesByType)
    {
        // Process single image types from ImageTags
        if (sourceItem.ImageTags?.AdditionalData != null)
        {
            foreach (var kvp in sourceItem.ImageTags.AdditionalData)
            {
                if (kvp.Value != null)
                {
                    sourceImagesByType[kvp.Key] = new List<ImageInfoDto>
                    {
                        new ImageInfoDto
                        {
                            ImageType = kvp.Key,
                            ImageIndex = 0,
                            Tag = kvp.Value.ToString()
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

            item.SourcePeopleValue = JsonSerializer.Serialize(sourcePeople);
        }
        else
        {
            item.SourcePeopleValue = "[]";
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

                    personDict["Type"] = person.Type.ToString();

                    if (personDict.ContainsKey("Name"))
                    {
                        localPeople.Add(personDict);
                    }
                }

                // Sort for consistent comparison (order shouldn't matter for people)
                localPeople.Sort(ComparePeopleDicts);

                item.LocalPeopleValue = JsonSerializer.Serialize(localPeople);
            }
            else
            {
                item.LocalPeopleValue = "[]";
            }
        }
        else
        {
            item.LocalPeopleValue = null;
        }
    }

    /// <summary>
    /// Sets studios values.
    /// Compares by studio name only to allow syncing between servers.
    /// </summary>
    private void SetStudiosValues(MetadataSyncItem item, BaseItemDto sourceItem, BaseItem? localItem)
    {
        // Serialize studios from source (just studio names)
        if (sourceItem.Studios != null && sourceItem.Studios.Count > 0)
        {
            // Sort for consistent comparison - extract studio names as strings
            var studioNames = new List<string>();
            foreach (var studio in sourceItem.Studios)
            {
                if (studio?.Name != null)
                {
                    studioNames.Add(studio.Name);
                }
            }

            studioNames.Sort(StringComparer.OrdinalIgnoreCase);
            item.SourceStudiosValue = JsonSerializer.Serialize(studioNames);
        }
        else
        {
            item.SourceStudiosValue = "[]";
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

                item.LocalStudiosValue = validStudios.Count > 0
                    ? JsonSerializer.Serialize(validStudios)
                    : "[]";
            }
            else
            {
                item.LocalStudiosValue = "[]";
            }
        }
        else
        {
            item.LocalStudiosValue = null;
        }
    }

    /// <summary>
    /// Gets the total count of items to process for progress tracking.
    /// </summary>
    public async Task<int> GetTotalItemCountAsync(
        SourceServerClient client,
        IEnumerable<LibraryMapping> libraryMappings,
        CancellationToken cancellationToken,
        bool includeFolderItems = false)
    {
        var totalCount = 0;

        foreach (var libraryMapping in libraryMappings)
        {
            if (!libraryMapping.IsEnabled || string.IsNullOrEmpty(libraryMapping.SourceLibraryId))
            {
                continue;
            }

            var sourceLibraryId = Guid.Parse(libraryMapping.SourceLibraryId);
            var count = await client.GetLibraryItemCountAsync(
                sourceLibraryId,
                cancellationToken).ConfigureAwait(false);

            totalCount += count;

            if (includeFolderItems)
            {
                var folderCount = await client.GetLibraryFolderItemCountAsync(
                    sourceLibraryId,
                    cancellationToken).ConfigureAwait(false);

                totalCount += folderCount;
            }
        }

        return totalCount;
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
