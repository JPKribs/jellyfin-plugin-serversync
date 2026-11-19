using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.MetadataSync;

/// <summary>
/// Builds <see cref="MetadataSyncItem"/> rows from source items, and refreshes one row's local side and image sizes for the dashboard.
/// </summary>
[PluginService(ServiceLifetime.Transient)]
public class MetadataSyncTableService
{
    private readonly ILogger<MetadataSyncTableService> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly ILocalItems _localItems;
    private readonly ILinkRegistry _links;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataSyncTableService"/> class.
    /// </summary>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="libraryManager">Reads local items and their people.</param>
    /// <param name="localItems">Finds local items by path.</param>
    /// <param name="links">Tells which local files are links.</param>
    public MetadataSyncTableService(
        ILogger<MetadataSyncTableService> logger,
        ILibraryManager libraryManager,
        ILocalItems localItems,
        ILinkRegistry links)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(localItems);
        ArgumentNullException.ThrowIfNull(links);
        _logger = logger;
        _libraryManager = libraryManager;
        _localItems = localItems;
        _links = links;
    }

    /// <summary>
    /// Reads the local side of a row again, so the dashboard shows live values and not those of the last scan.
    /// The source side and every hash are left alone.
    /// </summary>
    /// <param name="item">The row whose local blobs are replaced.</param>
    /// <param name="syncMetadata"><c>true</c> to re-read the metadata.</param>
    /// <param name="syncImages"><c>true</c> to re-read the images.</param>
    /// <param name="syncPeople"><c>true</c> to re-read the people.</param>
    /// <param name="syncStudios"><c>true</c> to re-read the studios.</param>
    /// <param name="syncGenres"><c>true</c> to include genres in the metadata.</param>
    /// <param name="syncTags"><c>true</c> to include tags in the metadata.</param>
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
        if (!Guid.TryParse(item.LocalItemId, out var localId)) return;

        var localItem = _libraryManager.GetItemById(localId);
        if (localItem == null) return;

        if (syncMetadata)
        {
            MetadataSyncMergeService.RebuildLocalMetadataBlob(item, localItem, syncGenres, syncTags);
        }

        if (syncImages)
        {
            MetadataSyncMergeService.RebuildLocalImagesBlob(item, localItem);
        }

        if (syncPeople)
        {
            MetadataSyncMergeService.RebuildLocalPeopleBlob(item, localItem, _libraryManager);
        }

        if (syncStudios)
        {
            MetadataSyncMergeService.RebuildLocalStudiosBlob(item, localItem);
        }
    }

    /// <summary>
    /// Adds each image's size and dimensions to a row's source manifest with one call to the source.
    /// A scan may store tags alone, which the dashboard would show as a size of 0.
    /// </summary>
    /// <param name="item">The row whose source image manifest is sized.</param>
    /// <param name="client">The client for the source server that holds the images.</param>
    /// <param name="cancellationToken">Cancels the call to the source.</param>
    /// <returns>A task that completes when the manifest is sized.</returns>
    public async Task EnrichSourceImageSizesAsync(
        MetadataSyncItem item,
        SourceServerClient client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        ArgumentNullException.ThrowIfNull(item);
        if (!Guid.TryParse(item.SourceItemId, out var sourceItemGuid)) return;

        item.Images.Source = await ImageUtilities.EnrichAsync(
            item.Images.Source,
            sourceItemGuid,
            client,
            _logger,
            item.ItemName ?? item.SourceItemId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds lookups of a local library's files and folders by path, so a scan can skip source items with no local match.
    /// Each link is also listed under its source path, and folders are not because their paths match on every server.
    /// Each item is the copy the library hands out by id, which is the one Sync writes to, never a folder's older cached child.
    /// </summary>
    /// <param name="localLibraryId">The id of the local library to read.</param>
    /// <returns>The leaves and the folders, each by path.</returns>
    public (Dictionary<string, BaseItem> Leaves, Dictionary<string, BaseItem> Folders) GetLocalItemsByPath(Guid localLibraryId)
    {
        if (_libraryManager.GetItemById(localLibraryId) is not Folder root)
        {
            return (new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase));
        }

        var (leaves, folders) = IndexByPath(root.GetRecursiveChildren(), _libraryManager.GetItemById);
        LinkPaths.AddAliases(leaves, _links);
        return (leaves, folders);
    }

    /// <summary>
    /// Sorts a library's items into files and folders by path, each as the copy the library hands out by id.
    /// A folder's cached child can be an older copy than the one Sync writes to, so it is used only when the library has no other.
    /// </summary>
    /// <param name="children">The library's items, as its folders list them.</param>
    /// <param name="byId">Gives the library's own copy of an item by id, or null.</param>
    /// <returns>The files and the folders, each by path.</returns>
    public static (Dictionary<string, BaseItem> Leaves, Dictionary<string, BaseItem> Folders) IndexByPath(IEnumerable<BaseItem> children, Func<Guid, BaseItem?> byId)
    {
        ArgumentNullException.ThrowIfNull(children);
        ArgumentNullException.ThrowIfNull(byId);
        var leaves = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in children)
        {
            var item = byId(child.Id) ?? child;
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
    /// Builds the metadata row for a source item, with the blobs of each category that is turned on.
    /// Returns null when the item has no path, the Library Filter leaves it out, or no local item matches its path.
    /// </summary>
    /// <param name="libraryMapping">The library mapping the item came through.</param>
    /// <param name="sourceItem">The item as the source returned it.</param>
    /// <param name="isFolder"><c>true</c> when the item is a folder.</param>
    /// <param name="syncMetadata"><c>true</c> to build the metadata blobs.</param>
    /// <param name="syncImages"><c>true</c> to build the image manifests.</param>
    /// <param name="syncPeople"><c>true</c> to build the people blobs.</param>
    /// <param name="syncStudios"><c>true</c> to build the studio blobs.</param>
    /// <param name="syncGenres"><c>true</c> to include genres in the metadata.</param>
    /// <param name="syncTags"><c>true</c> to include tags in the metadata.</param>
    /// <param name="priorSourceImagesJson">The last scan's source image manifest for this key, whose sizes carry over while the tags match, or null.</param>
    /// <param name="priorLocalImagesJson">The last scan's local image manifest for this key, which holds the tags matched so far, or null.</param>
    /// <param name="deepImageVerification"><c>true</c> to read each image's size from the source.</param>
    /// <param name="client">The client for the source server, used to size its images.</param>
    /// <param name="cancellationToken">Cancels the calls that size the source images.</param>
    /// <param name="knownLocalItem">Finds a local item by path and whether it is a folder among the items the caller already holds, or null to ask the library for each.</param>
    /// <param name="keptSourcePeopleJson">The source cast the last scan read, used when this scan did not fetch the cast, or null to read it from <paramref name="sourceItem"/>.</param>
    /// <returns>The row, or null when the item is not to be tracked.</returns>
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
        string? priorSourceImagesJson,
        string? priorLocalImagesJson,
        bool deepImageVerification,
        SourceServerClient client,
        CancellationToken cancellationToken,
        Func<string, bool, BaseItem?>? knownLocalItem = null,
        string? keptSourcePeopleJson = null)
    {
        ArgumentNullException.ThrowIfNull(libraryMapping);
        ArgumentNullException.ThrowIfNull(sourceItem);

        var sourcePath = sourceItem.Path;
        // A virtual season or artist has no path to match locally.
        if (string.IsNullOrEmpty(sourcePath))
        {
            return null;
        }

        // A filtered item returns null so the scan prunes any row it had.
        if (libraryMapping.FilterMode != LibraryFilterMode.AllowAll
            && libraryMapping.FilteredItems?.Count > 0
            && PathUtilities.IsItemFiltered(sourcePath, libraryMapping.SourceRootPath, libraryMapping.FilterMode, libraryMapping.FilteredItems))
        {
            return null;
        }

        var sourceItemId = sourceItem.Id!.Value.ToString("N", CultureInfo.InvariantCulture);
        var localPath = PathUtilities.TranslatePath(sourcePath, libraryMapping.SourceRootPath, libraryMapping.LocalRootPath);

        // A scan already holds every local item by path, so the library is asked only for one it does not hold.
        var localItem = knownLocalItem?.Invoke(localPath, isFolder) ?? _localItems.FindByPath(localPath, isFolder: isFolder);
        if (localItem == null)
        {
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
            MetadataSyncMergeService.MergeMetadataFields(item, sourceItem, localItem, syncGenres, syncTags);
        }

        if (syncImages)
        {
            await MetadataSyncMergeService.MergeImagesAsync(
                item, sourceItem, localItem, client,
                priorSourceImagesJson, priorLocalImagesJson, deepImageVerification,
                _logger, cancellationToken).ConfigureAwait(false);
        }

        if (syncPeople)
        {
            MetadataSyncMergeService.MergePeople(item, sourceItem, localItem, _libraryManager);
            if (keptSourcePeopleJson is not null)
            {
                item.People.UpdateSource(keptSourcePeopleJson);
            }
        }

        if (syncStudios)
        {
            MetadataSyncMergeService.MergeStudios(item, sourceItem, localItem);
        }

        return item;
    }
}
