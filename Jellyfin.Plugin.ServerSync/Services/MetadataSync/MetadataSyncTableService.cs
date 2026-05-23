using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
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
    /// Builds a <see cref="MetadataSyncItem"/> from a source item, or null if
    /// the source path is missing, the library filter excludes it, or no
    /// local item matches by translated path. Category flags gate which
    /// blobs are populated; <paramref name="syncGenres"/> / <paramref name="syncTags"/>
    /// gate the corresponding subfields inside the metadata blob.
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
            MetadataSyncMergeService.MergeMetadataFields(item, sourceItem, localItem, syncGenres, syncTags);
        }

        if (syncImages)
        {
            await MetadataSyncMergeService.MergeImagesAsync(item, sourceItem, localItem, client, _logger, cancellationToken).ConfigureAwait(false);
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
