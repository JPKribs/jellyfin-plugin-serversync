using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Build orchestration for Metadata Sync records. Owns the shell of a
/// <see cref="MetadataSyncItem"/> (IDs, paths, library mapping) and delegates
/// per-category source/local blob building to <see cref="MetadataSyncMergeService"/>.
/// Also exposes per-modal helpers (<see cref="RefreshLocalSnapshot"/>,
/// <see cref="EnrichSourceImageSizesAsync"/>) and the per-library local
/// enumeration the refresh task uses to skip source items with no local
/// correlate.
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
    /// <param name="logger">Logger instance.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="localItems">Finds local items by path.</param>
    /// <param name="links">Tells which local files are links.</param>
    public MetadataSyncTableService(
        ILogger<MetadataSyncTableService> logger,
        ILibraryManager libraryManager,
        ILocalItems localItems,
        ILinkRegistry links)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _localItems = localItems;
        _links = links;
    }

    /// <summary>
    /// Re-reads the local side of a record so the modal shows live data
    /// rather than the snapshot captured at the last metadata refresh. Call
    /// this from per-item endpoints, without it, a successful Sync apply
    /// updates local state in Jellyfin but the modal keeps showing the
    /// pre-sync local blob until the user re-runs the full Refresh task.
    /// Source-side fields (and all hashes) are left untouched, so this is
    /// a read-only refresh of the displayed Local columns.
    /// </summary>
    /// <param name="item">The record whose local side is re-read.</param>
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
    /// Enriches the source-side image manifest in <paramref name="item"/>
    /// with Size / Width / Height fetched from the source server. The refresh
    /// task builds the source manifest from <c>BaseItemDto.ImageTags</c> for
    /// performance, tag-only with Size=0, so the modal would otherwise
    /// render source-side as "0 B" while local has a real KB value. This
    /// per-modal-open helper compensates with one HTTP call.
    /// </summary>
    /// <param name="item">The record whose source images are enriched.</param>
    /// <param name="client">The client for the source server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task EnrichSourceImageSizesAsync(
        MetadataSyncItem item,
        SourceServerClient client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

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

    /// <summary>
    /// Builds a path → <see cref="BaseItem"/> lookup for the given local
    /// library folder. Used by the refresh task to skip over source items
    /// that have no local correlate without paying for a heavy bulk fetch
    /// of source metadata first. Folders and leaves are returned in
    /// separate dictionaries so callers can match by item type.
    /// </summary>
    /// <param name="localLibraryId">The local library to read.</param>
    /// <returns>The leaves and the folders, each by path.</returns>
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

        // Folders keep the same path on every server, so only files can be links.
        LinkPaths.AddAliases(leaves, _links);
        return (leaves, folders);
    }

    /// <summary>
    /// Builds a <see cref="MetadataSyncItem"/> from a source item, or null if
    /// the source path is missing, the library filter excludes it, or no
    /// local item matches by translated path. Category flags gate which
    /// blobs are populated. <paramref name="syncGenres"/> / <paramref name="syncTags"/>
    /// gate the corresponding subfields inside the metadata blob.
    /// <paramref name="priorSourceImagesJson"/> is the previous refresh's
    /// source image manifest for this key, image sizes carry forward from it
    /// when tags are unchanged, skipping the per-item enrichment HTTP call.
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
    /// <param name="priorSourceImagesJson">The last refresh's source image manifest for this key, or null.</param>
    /// <param name="deepImageVerification"><c>true</c> to read each image's size from the source.</param>
    /// <param name="client">The client for the source server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The record, or null when the item is not to be tracked.</returns>
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
        bool deepImageVerification,
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

        var localItem = _localItems.FindByPath(localPath, isFolder: isFolder);
        if (localItem == null)
        {
            // No local match, skip. If a row existed previously the base
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
            MetadataSyncMergeService.MergeMetadataFields(item, sourceItem, localItem, syncGenres, syncTags);
        }

        if (syncImages)
        {
            await MetadataSyncMergeService.MergeImagesAsync(
                item, sourceItem, localItem, client,
                priorSourceImagesJson, deepImageVerification,
                _logger, cancellationToken).ConfigureAwait(false);
        }

        if (syncPeople)
        {
            MetadataSyncMergeService.MergePeople(item, sourceItem, localItem, _libraryManager);
        }

        if (syncStudios)
        {
            MetadataSyncMergeService.MergeStudios(item, sourceItem, localItem);
        }

        return item;
    }
}
