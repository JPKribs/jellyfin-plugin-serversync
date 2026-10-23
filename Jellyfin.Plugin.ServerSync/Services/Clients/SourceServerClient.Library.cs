using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Library listings, item lookups, file and companion downloads, and the shared paging loop.
/// </summary>
public partial class SourceServerClient
{
    /// <summary>
    /// Fetches the Content-sync leaves for a single whitelisted item: returns
    /// the item itself if it's a leaf, or walks its descendants folder-by-
    /// folder if it's a folder type. The folder walk avoids the
    /// <c>ParentId</c>+<c>Recursive=true</c> path because Jellyfin keeps the
    /// literal <c>ParentId</c> filter on recursive queries, which returns
    /// nothing for a Series since episodes carry the Season's ID, not the
    /// Series'.
    /// </summary>
    public async Task<List<BaseItemDto>> GetWhitelistedItemLeavesAsync(
        Guid whitelistedId,
        Guid? playlistUserId = null,
        CancellationToken cancellationToken = default)
    {
        var leafTypes = new HashSet<BaseItemDto_Type?>
        {
            BaseItemDto_Type.Movie,
            BaseItemDto_Type.Episode,
            BaseItemDto_Type.Audio,
            BaseItemDto_Type.Video
        };

        var result = new List<BaseItemDto>();
        var seen = new HashSet<Guid>();

        BaseItemDto? rootItem;
        try
        {
            var client = GetApiClient();
            var rootPage = await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.Ids = new Guid?[] { whitelistedId };
                    config.QueryParameters.Fields = new[] { ItemFields.Path, ItemFields.DateCreated, ItemFields.MediaSources };
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            rootItem = rootPage?.Items?.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
        {
            // Any 4xx/5xx: bubble up so the Refresh aborts before prune.
            _logger.LogWarning(ex, "Source server {Status} on whitelist root {Id}. Aborting refresh to protect tracking rows", ex.ResponseStatusCode, whitelistedId);
            throw;
        }
        catch (Exception ex)
        {
            // Transport faults (connection reset, DNS, TLS) and deserialization
            // errors land here, Kiota only wraps HTTP status errors as
            // ApiException. Returning an empty list would make every leaf under
            // this entry look "missing from source" and schedule its files for
            // deletion, so bubble up instead.
            _logger.LogWarning(ex, "Whitelist: failed to fetch item {Id}; aborting to protect tracking rows", whitelistedId);
            throw;
        }

        if (rootItem == null || !rootItem.Id.HasValue)
        {
            // An entry that no longer exists on the source (deleted
            // collection, deleted item) returns 200-with-nothing, treating
            // that as an empty expansion would mark every leaf under it as
            // removed and schedule the files for deletion. Fail the entry
            // instead. The refresh marks the source unavailable, keeps
            // syncing the other entries, and skips pruning until the user
            // removes the dead entry from the whitelist.
            throw new InvalidOperationException(
                $"Whitelist entry {whitelistedId} no longer exists on the source server, remove it from the mapping's whitelist to resume pruning");
        }

        if (leafTypes.Contains(rootItem.Type))
        {
            result.Add(rootItem);
            seen.Add(rootItem.Id.Value);
            return result;
        }

        // Folder-like (Series / Season / BoxSet / Album / Artist): walk
        // children breadth-first one folder at a time, collecting leaves.
        // Playlists are user-scoped and don't answer ParentId child queries,
        // so their members come from the playlist endpoint and seed the same
        // walk (members are usually leaves already. Folders still expand).
        var queue = new Queue<Guid>();
        if (rootItem.Type == BaseItemDto_Type.Playlist)
        {
            var members = await GetPlaylistItemsAsync(rootItem.Id.Value, playlistUserId, cancellationToken).ConfigureAwait(false);
            foreach (var member in members)
            {
                if (!member.Id.HasValue)
                {
                    continue;
                }

                if (leafTypes.Contains(member.Type))
                {
                    if (seen.Add(member.Id.Value))
                    {
                        result.Add(member);
                    }
                }
                else if (seen.Add(member.Id.Value))
                {
                    queue.Enqueue(member.Id.Value);
                }
            }
        }
        else
        {
            queue.Enqueue(rootItem.Id.Value);
        }

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parentId = queue.Dequeue();

            var parentSeen = new HashSet<Guid>();
            var client = GetApiClient();
            await PageThroughAsync(
                async (startIndex, pageSize, ct) =>
                {
                    try
                    {
                        return await client.Items.GetAsync(
                            config =>
                            {
                                config.QueryParameters.ParentId = parentId;
                                config.QueryParameters.Fields = new[] { ItemFields.Path, ItemFields.DateCreated, ItemFields.MediaSources };
                                config.QueryParameters.SortBy = new[] { ItemSortBy.SortName };
                                config.QueryParameters.StartIndex = startIndex;
                                config.QueryParameters.Limit = pageSize;
                            },
                            cancellationToken: ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
                    {
                        // Any 4xx/5xx: bubble up. Ending the list here would silently
                        // truncate the leaf list, which the caller can't tell
                        // apart from "this folder really has no more children" , 
                        // exactly the kind of silent partial result that drives
                        // the prune massacre.
                        _logger.LogWarning(ex, "Source server {Status} listing whitelist children of {ParentId}. Aborting refresh to protect tracking rows", ex.ResponseStatusCode, parentId);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Transport faults reach here as HttpRequestException, not
                        // ApiException. Ending the list would truncate it the
                        // same way a swallowed 5xx would, bubble up instead.
                        _logger.LogWarning(ex, "Whitelist: failed to fetch children of {ParentId}; aborting to protect tracking rows", parentId);
                        throw;
                    }
                },
                1000,
                child =>
                {
                    if (!child.Id.HasValue)
                    {
                        return;
                    }

                    // A repeated ID within one parent means page boundaries
                    // shifted mid-scan. The matching skip on the other side is
                    // invisible and would be pruned as "removed". Throwing
                    // marks the source unavailable, which skips pruning.
                    if (!parentSeen.Add(child.Id.Value))
                    {
                        throw new InvalidOperationException(
                            $"Whitelist children of {parentId} changed during enumeration (duplicate item across pages)");
                    }

                    // Global dedupe stays separate: BoxSet children legitimately
                    // repeat across whitelist entries.
                    if (!seen.Add(child.Id.Value))
                    {
                        return;
                    }

                    if (leafTypes.Contains(child.Type))
                    {
                        result.Add(child);
                    }
                    else
                    {
                        // Folder, descend.
                        queue.Enqueue(child.Id.Value);
                    }
                },
                PageEnd.NullOrEmptyEnds,
                (expected, fetched) => $"Whitelist children of {parentId} changed during enumeration (expected {expected}, fetched {fetched})",
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Gets items from a library with pagination support.
    /// </summary>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="startIndex">Starting index for pagination.</param>
    /// <param name="limit">Maximum items to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with items.</returns>
    public async Task<BaseItemDtoQueryResult?> GetLibraryItemsAsync(
        Guid libraryId,
        int startIndex = 0,
        int limit = 100,
        DateTime? changedSince = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            return await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video };
                    config.QueryParameters.Fields = new[] { ItemFields.Path, ItemFields.DateCreated, ItemFields.MediaSources };
                    config.QueryParameters.MinDateLastSaved = changedSince.HasValue ? new DateTimeOffset(Utilities.UtcTime.AsUtc(changedSince.Value)) : null;
                    // The API has no by-Id sort, so a fully stable order isn't
                    // possible. The multi-key sort makes tie order deterministic
                    // and PaginatedFetchUtility's TotalRecordCount check catches
                    // mid-enumeration catalog changes so they can't turn into
                    // silent skips that the prune would read as removals.
                    config.QueryParameters.SortBy = new[] { ItemSortBy.DateCreated, ItemSortBy.SortName, ItemSortBy.IndexNumber };
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
        {
            // Any 4xx/5xx: bubble up so the Refresh aborts before prune.
            _logger.LogWarning(ex, "Source server {Status} getting items from library {LibraryId}. Aborting refresh to protect tracking rows", ex.ResponseStatusCode, libraryId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get items from library {LibraryId}", libraryId);
            return null;
        }
    }

    /// <summary>
    /// Lists collections (BoxSets) on the source server for the whitelist
    /// picker. Collections live outside libraries, so this is a server-wide
    /// recursive BoxSet query.
    /// </summary>
    /// <param name="searchTerm">Optional search term.</param>
    /// <param name="startIndex">Starting index for pagination.</param>
    /// <param name="limit">Maximum items to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with collections.</returns>
    public async Task<BaseItemDtoQueryResult?> GetCollectionsAsync(
        string? searchTerm = null,
        int startIndex = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            return await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.BoxSet };
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.Fields = new[] { ItemFields.Path, ItemFields.Overview, ItemFields.DateCreated, ItemFields.ChildCount };
                    config.QueryParameters.SortBy = new[] { ItemSortBy.SortName };
                    config.QueryParameters.SortOrder = new[] { SortOrder.Ascending };
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                    if (!string.IsNullOrWhiteSpace(searchTerm))
                    {
                        config.QueryParameters.SearchTerm = searchTerm;
                    }
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list collections from source server");
            return null;
        }
    }

    /// <summary>
    /// Fetches a playlist's members, paged, failing loud on any error or
    /// mid-scan inconsistency, a truncated member list would mark the
    /// missing items as removed and schedule their files for deletion.
    /// </summary>
    private async Task<List<BaseItemDto>> GetPlaylistItemsAsync(Guid playlistId, Guid? userId, CancellationToken cancellationToken, bool distinct = true)
    {
        var members = new List<BaseItemDto>();
        var memberIds = new HashSet<Guid>();
        var client = GetApiClient();
        await PageThroughAsync(
            async (startIndex, pageSize, ct) =>
            {
                try
                {
                    return await client.Playlists[playlistId].Items.GetAsync(
                        config =>
                        {
                            config.QueryParameters.UserId = userId;
                            config.QueryParameters.Fields = new[] { ItemFields.Path, ItemFields.DateCreated, ItemFields.MediaSources };
                            config.QueryParameters.StartIndex = startIndex;
                            config.QueryParameters.Limit = pageSize;
                        },
                        cancellationToken: ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
                {
                    _logger.LogWarning(ex, "Source server {Status} listing playlist {Id} members. Aborting refresh to protect tracking rows", ex.ResponseStatusCode, playlistId);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Playlist: failed to fetch members of {Id}; aborting to protect tracking rows", playlistId);
                    throw;
                }
            },
            1000,
            item =>
            {
                // A playlist may legitimately hold the same item more than
                // once, so dedupe the returned members. The mid-scan
                // consistency check uses the RAW count (which includes
                // those legitimate duplicates) against TotalRecordCount, so a
                // duplicate never looks like a shifted catalog.
                if (item.Id.HasValue && (!distinct || memberIds.Add(item.Id.Value)))
                {
                    members.Add(item);
                }
            },
            PageEnd.NullOrEmptyEnds,
            (expected, fetched) => $"Playlist {playlistId} changed during enumeration (expected {expected} members, fetched {fetched})",
            cancellationToken,
            stopAtTotal: false).ConfigureAwait(false);

        return members;
    }

    /// <summary>
    /// Lists playlists on the source server for the whitelist picker.
    /// Playlists, like collections, live outside libraries.
    /// </summary>
    /// <param name="searchTerm">Optional search term.</param>
    /// <param name="startIndex">Starting index for pagination.</param>
    /// <param name="limit">Maximum items to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with playlists.</returns>
    public async Task<BaseItemDtoQueryResult?> GetPlaylistsAsync(
        string? searchTerm = null,
        int startIndex = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            return await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Playlist };
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.Fields = new[] { ItemFields.Path, ItemFields.Overview, ItemFields.DateCreated, ItemFields.ChildCount };
                    config.QueryParameters.SortBy = new[] { ItemSortBy.SortName };
                    config.QueryParameters.SortOrder = new[] { SortOrder.Ascending };
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                    if (!string.IsNullOrWhiteSpace(searchTerm))
                    {
                        config.QueryParameters.SearchTerm = searchTerm;
                    }
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list playlists from source server");
            return null;
        }
    }

    /// <summary>
    /// Gets top-level items from a library (non-recursive) for browsing/filtering UI.
    /// Returns series, movies, or top-level folders depending on library type.
    /// </summary>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="searchTerm">Optional search term to filter results.</param>
    /// <param name="startIndex">Starting index for pagination.</param>
    /// <param name="limit">Maximum items to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with top-level items.</returns>
    public async Task<BaseItemDtoQueryResult?> GetTopLevelLibraryItemsAsync(
        Guid libraryId,
        string? searchTerm = null,
        int startIndex = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            return await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Fields = new[] { ItemFields.Path, ItemFields.Overview, ItemFields.DateCreated };
                    config.QueryParameters.SortBy = new[] { ItemSortBy.SortName };
                    config.QueryParameters.SortOrder = new[] { SortOrder.Ascending };
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                    config.QueryParameters.EnableImageTypes = new[] { ImageType.Primary };
                    config.QueryParameters.ImageTypeLimit = 1;

                    if (!string.IsNullOrWhiteSpace(searchTerm))
                    {
                        // When searching, use recursive so Jellyfin's search engine works,
                        // but restrict to top-level container types only (no episodes/seasons/tracks)
                        config.QueryParameters.SearchTerm = searchTerm;
                        config.QueryParameters.Recursive = true;
                        config.QueryParameters.IncludeItemTypes = new[]
                        {
                            BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.BoxSet,
                            BaseItemKind.MusicAlbum, BaseItemKind.MusicArtist
                        };
                    }
                    else
                    {
                        // Non-recursive browse, include standalone files too (Audio, Video)
                        // since they appear at the top level in some library types
                        config.QueryParameters.Recursive = false;
                        config.QueryParameters.IncludeItemTypes = new[]
                        {
                            BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.BoxSet,
                            BaseItemKind.MusicAlbum, BaseItemKind.MusicArtist,
                            BaseItemKind.Audio, BaseItemKind.Video
                        };
                    }
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get top-level items from library {LibraryId}", libraryId);
            return null;
        }
    }

    /// <summary>
    /// Gets the total count of items in a library without fetching item details.
    /// </summary>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Total item count or 0 if failed.</returns>
    public async Task<int> GetLibraryItemCountAsync(Guid libraryId, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            var result = await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video };
                    config.QueryParameters.StartIndex = 0;
                    config.QueryParameters.Limit = 0;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return result?.TotalRecordCount ?? 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
        {
            // Any 4xx/5xx: bubble up so the Refresh aborts before prune.
            _logger.LogWarning(ex, "Source server {Status} getting item count for library {LibraryId}. Aborting refresh to protect tracking rows", ex.ResponseStatusCode, libraryId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get item count for library {LibraryId}", libraryId);
            return 0;
        }
    }

    /// <summary>
    /// Lightweight discovery fetch, returns only Id and Path for items in a
    /// library. Used by Metadata refresh to determine which source items have
    /// a local correlate before paying for the full metadata payload. The
    /// per-page response is dramatically smaller than a full metadata page
    /// (no Genres/Studios/
    /// Tags/People/Overview/etc.), so the discovery scan is cheap even on
    /// huge libraries.
    /// Every failure throws rather than answering null. Callers page until a short or empty page, so
    /// a null read as the end of the library would make every item past a failed page look removed
    /// from the source and be pruned.
    /// </summary>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="includeTypes">Item kinds to list.</param>
    /// <param name="startIndex">Starting index for pagination.</param>
    /// <param name="limit">Maximum items to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page the server answered with.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the server could not be asked or gave no answer.</exception>
    public async Task<BaseItemDtoQueryResult> GetLibraryItemPathsAsync(
        Guid libraryId,
        BaseItemKind[] includeTypes,
        int startIndex = 0,
        int limit = 1000,
        DateTime? changedSince = null,
        CancellationToken cancellationToken = default)
    {
        BaseItemDtoQueryResult? page;
        try
        {
            var client = GetApiClient();
            page = await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = includeTypes;
                    config.QueryParameters.Fields = new[] { ItemFields.Path };
                    config.QueryParameters.MinDateLastSaved = changedSince.HasValue ? new DateTimeOffset(Utilities.UtcTime.AsUtc(changedSince.Value)) : null;
                    config.QueryParameters.SortBy = new[] { ItemSortBy.DateCreated, ItemSortBy.SortName, ItemSortBy.IndexNumber };
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
        {
            // Any 4xx/5xx: bubble up so the Refresh aborts before prune.
            _logger.LogWarning(ex, "Source server {Status} getting item paths from library {LibraryId}. Aborting refresh to protect tracking rows", ex.ResponseStatusCode, libraryId);
            throw;
        }
        catch (Exception ex)
        {
            // A transport fault is no answer at all, the same as a 5xx. Swallowing it handed callers a
            // null they read as the end of the library.
            _logger.LogWarning(ex, "Failed to get item paths from library {LibraryId} at index {Index}. Aborting refresh to protect tracking rows", libraryId, startIndex);
            throw new InvalidOperationException($"Failed to get item paths from library {libraryId} at index {startIndex}: {ex.Message}", ex);
        }

        return page ?? throw new InvalidOperationException($"Source server returned no page for library {libraryId} at index {startIndex}");
    }

    /// <summary>
    /// Batch-fetches full metadata for a specific set of item IDs. Used by
    /// Metadata refresh after the discovery pass: only items that exist
    /// locally are fetched with their heavy <see cref="ItemFields"/>, so we
    /// don't pay for source items the user doesn't have.
    /// </summary>
    public async Task<List<BaseItemDto>> GetItemsByIdsAsync(
        IReadOnlyList<Guid> ids,
        ItemFields[] fields,
        int batchSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = new List<BaseItemDto>();
        if (ids.Count == 0)
        {
            return result;
        }

        for (var i = 0; i < ids.Count; i += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = ids.Skip(i).Take(batchSize).Cast<Guid?>().ToArray();

            try
            {
                var client = GetApiClient();
                var response = await client.Items.GetAsync(
                    config =>
                    {
                        config.QueryParameters.Ids = chunk;
                        config.QueryParameters.Fields = fields;
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (response?.Items != null)
                {
                    result.AddRange(response.Items);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
            {
                // Any 4xx/5xx means the source didn't give us a real answer , 
                // not that the requested items don't exist. Letting this
                // swallow returns a partial result and downstream pruning
                // then deletes every local row that "wasn't seen this run."
                // Bubble up so the refresh aborts before the prune step.
                _logger.LogWarning(ex, "Source server {Status} fetching items batch at index {Index}. Aborting refresh to protect tracking rows", ex.ResponseStatusCode, i);
                throw;
            }
            catch (Exception ex)
            {
                // Transport faults (HttpRequestException, deserialization) are
                // "no real answer" just like a 5xx, a swallowed chunk here
                // gets its rows pruned and loses the user's Ignored overrides.
                _logger.LogWarning(ex, "Failed to fetch items batch starting at index {Index}. Aborting refresh to protect tracking rows", i);
                throw;
            }
        }

        return result;
    }

    /// <summary>
    /// Reads one item's details, failing when the server cannot be asked and answering null only when it
    /// answers that it holds no such item. A caller can then tell an item that is gone, which needs
    /// nothing more, from one it could not reach, which is worth asking again.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The item, or null when the server holds none by that id.</returns>
    public async Task<BaseItemDto?> FindItemDetailsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var client = GetApiClient();
        var result = await client.Items.GetAsync(
            config =>
            {
                config.QueryParameters.Ids = new Guid?[] { itemId };
                config.QueryParameters.Fields = new[] { ItemFields.Path, ItemFields.DateCreated, ItemFields.MediaSources, ItemFields.MediaStreams };
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result?.Items?.FirstOrDefault();
    }

    /// <summary>
    /// Downloads a file from the source server.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stream of file content and the response Content-Length when the server provided one.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the download could not start, with the cause in the message.</exception>
    public async Task<(Stream Stream, long? ContentLength)> DownloadFileAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var download = await DownloadFileFromAsync(itemId, 0, null, cancellationToken).ConfigureAwait(false);
        return (download.Stream, download.ContentLength);
    }

    /// <summary>
    /// Downloads a file from the source server, from a byte offset when the source can resume it. The
    /// range is asked for only with the validator an earlier answer carried, through If-Range, so a file
    /// that changed on the source since then comes back whole instead of being stitched onto stale bytes.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="from">The byte to start from, 0 for the whole file.</param>
    /// <param name="validator">The validator from the earlier answer, or null when there was none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The download, saying where its bytes start.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the download could not start, with the cause in the message.</exception>
    public async Task<SourceDownload> DownloadFileFromAsync(Guid itemId, long from, string? validator, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage? response = null;
        HttpRequestMessage? request = null;
        var ownershipTransferred = false;
        try
        {
            // /Items/{id}/Download, direct-download URL. Authentication is
            // via the Authorization header only. Do NOT mix in api_key /
            // ApiKey query parameters or X-Emby-Token headers. Per the
            // Jellyfin auth spec (10.11+), sending multiple tokens in one
            // request has undefined precedence and may be rejected outright
            // when EnableLegacyAuthorization=false on the source.
            var url = $"{_serverUrl}/Items/{itemId}/Download";
            RangeConditionHeaderValue? ifRange = null;
            var resume = from > 0 && SourceDownload.TryParseIfRange(validator, out ifRange);
            request = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuthorizationHeader(request);
            if (resume)
            {
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, null);
                request.Headers.IfRange = ifRange;
            }

            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (resume && response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // The file is shorter than what was kept, so it changed. Start over.
                DisposeQuiet(response);
                DisposeQuiet(request);
                response = null;
                request = null;
                ownershipTransferred = true;
                return await DownloadFileFromAsync(itemId, 0, null, cancellationToken).ConfigureAwait(false);
            }

            response.EnsureSuccessStatusCode();

            var range = response.Content.Headers.ContentRange;
            var offset = resume && response.StatusCode == System.Net.HttpStatusCode.PartialContent && range?.From == from ? from : 0;
            if (response.StatusCode == System.Net.HttpStatusCode.PartialContent && offset == 0)
            {
                throw new InvalidOperationException($"the source answered a range this server did not ask for ({range})");
            }

            var contentLength = response.Content.Headers.ContentLength;
            var total = offset > 0 ? range?.Length ?? (contentLength + offset) : contentLength;
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            // Transfer ownership of request and response to the stream wrapper
            var wrapper = new ResponseDisposingStream(stream, response, request);
            ownershipTransferred = true;
            return new SourceDownload(wrapper, contentLength, total, offset, SourceDownload.ValidatorOf(response));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download item {ItemId}", itemId);
            // Re-throw with a context-rich message so the per-row Reason
            // field surfaces what actually went wrong (timeout, 5xx,
            // connection refused, SSL handshake, etc.) instead of the
            // generic "No response from server". Original exception is
            // preserved as InnerException for stack-trace logging.
            throw new InvalidOperationException(
                $"Source download failed for item {itemId}: {ex.GetType().Name}: {ex.Message}",
                ex);
        }
        finally
        {
            if (!ownershipTransferred)
            {
                DisposeQuiet(response);
                DisposeQuiet(request);
            }
        }
    }

    /// <summary>
    /// Gets external subtitle companion files for an item. Restricted to
    /// subtitle streams: they are the only external stream type Jellyfin
    /// exposes a download route for
    /// (<c>/Videos/{item}/{mediaSource}/Subtitles/{index}/Stream.{format}</c>).
    /// External audio tracks are skipped with a log, the previous
    /// path-parameter approach silently served the item's main video file in
    /// their place, corrupting the companion on disk.
    /// A failure to read the item throws instead of answering an empty list. An empty list means the
    /// source has no external subtitles, and a caller replacing a file needs to tell that apart from
    /// a source it could not ask.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of companion file info, empty when the item has none or the source holds no such item.</returns>
    public async Task<List<CompanionFileInfo>> GetCompanionFilesAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var companions = new List<CompanionFileInfo>();

        var item = await FindItemDetailsAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (item?.MediaSources == null || item.MediaSources.Count == 0)
        {
            return companions;
        }

        var mediaSource = item.MediaSources.FirstOrDefault();
        if (mediaSource?.MediaStreams == null)
        {
            return companions;
        }

        var skippedNonSubtitle = 0;
        foreach (var stream in mediaSource.MediaStreams)
        {
            if (stream.IsExternal != true || string.IsNullOrEmpty(stream.Path))
            {
                continue;
            }

            if (stream.Type != MediaStream_Type.Subtitle)
            {
                skippedNonSubtitle++;
                continue;
            }

            companions.Add(new CompanionFileInfo
            {
                SourcePath = stream.Path!,
                FileName = Path.GetFileName(stream.Path)!,
                Language = stream.Language,
                Codec = stream.Codec,
                IsExternal = true,
                StreamIndex = stream.Index ?? 0,
                MediaSourceId = mediaSource.Id ?? itemId.ToString("N")
            });
        }

        if (skippedNonSubtitle > 0)
        {
            _logger.LogInformation(
                "Item {ItemId} has {Count} external non-subtitle stream(s) (e.g. audio) that cannot be downloaded via the API. Skipping",
                itemId, skippedNonSubtitle);
        }

        return companions;
    }

    /// <summary>
    /// Downloads an external subtitle via the canonical
    /// <c>/Videos/{item}/{mediaSource}/Subtitles/{index}/Stream.{format}</c>
    /// route. The previously used
    /// <c>/Videos/{id}/Subtitles/Stream?path=…</c> shape returns 405 on
    /// Jellyfin 10.11, and its <c>/Items/{id}/File?path=…</c> fallback
    /// ignores the path parameter and serves the item's main media file , 
    /// verified against a live 10.11 server, which wrote video bytes into
    /// <c>.srt</c> companions.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="companion">Companion stream to download.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stream of file content or null on failure.</returns>
    public async Task<Stream?> DownloadCompanionFileAsync(Guid itemId, CompanionFileInfo companion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(companion);
        HttpResponseMessage? response = null;
        HttpRequestMessage? request = null;
        var ownershipTransferred = false;
        try
        {
            // Stream.{format}: use the source file's own extension so the
            // server returns the subtitle in its original format instead of
            // converting. Codec name is the fallback for extension-less paths
            // (subrip files carry the .srt extension, so map that one).
            var format = Path.GetExtension(companion.SourcePath).TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(format))
            {
                format = string.Equals(companion.Codec, "subrip", StringComparison.OrdinalIgnoreCase)
                    ? "srt"
                    : companion.Codec?.ToLowerInvariant() ?? "srt";
            }

            var url = $"{_serverUrl}/Videos/{itemId}/{Uri.EscapeDataString(companion.MediaSourceId)}/Subtitles/{companion.StreamIndex}/Stream.{Uri.EscapeDataString(format)}";
            request = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuthorizationHeader(request);

            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            // Transfer ownership of request and response to the stream wrapper
            var wrapper = new ResponseDisposingStream(stream, response, request);
            ownershipTransferred = true;
            return wrapper;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download companion subtitle {FilePath} (stream {Index}) for {ItemId}", companion.SourcePath, companion.StreamIndex, itemId);
            return null;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                DisposeQuiet(response);
                DisposeQuiet(request);
            }
        }
    }

    /// <summary>How a page loop treats an answer with no items.</summary>
    private enum PageEnd
    {
        /// <summary>An empty page ends the list. A missing answer ends it too.</summary>
        NullOrEmptyEnds,

        /// <summary>An empty page ends the list. The fetch throws for a missing answer.</summary>
        EmptyEnds
    }

    /// <summary>
    /// Walks a paged listing to its end. The fetch owns its error handling, which differs by caller:
    /// most throw rather than end early, since a list cut short reads as items removed from the source.
    /// The loop stops on an empty page, a short page, or, unless told otherwise, once the reported
    /// total is reached. With <paramref name="describeShift"/> set, it also throws when the number of
    /// items fetched differs from the total the first page reported, which means the listing changed
    /// underneath the walk and an item may have been skipped at a page boundary.
    /// </summary>
    /// <param name="fetchPage">Fetches one page from a start index and size.</param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="onItem">Handles each item, in order. It may throw to abort the walk.</param>
    /// <param name="end">How an answer with no items is read.</param>
    /// <param name="describeShift">Describes a changed listing from the expected and fetched counts, or null to skip the check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="stopAtTotal">Whether reaching the reported total ends the walk before a short page does.</param>
    /// <returns>How many items were fetched.</returns>
    private static async Task<int> PageThroughAsync(
        Func<int, int, CancellationToken, Task<BaseItemDtoQueryResult?>> fetchPage,
        int pageSize,
        Action<BaseItemDto> onItem,
        PageEnd end,
        Func<int, int, string>? describeShift,
        CancellationToken cancellationToken,
        bool stopAtTotal = true)
    {
        var startIndex = 0;
        var rawFetched = 0;
        int? expectedTotal = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await fetchPage(startIndex, pageSize, cancellationToken).ConfigureAwait(false);
            if (page?.Items is null || page.Items.Count == 0)
            {
                if (page is null && end == PageEnd.EmptyEnds)
                {
                    throw new InvalidOperationException($"No answer for the page at index {startIndex}");
                }

                break;
            }

            expectedTotal ??= page.TotalRecordCount;
            rawFetched += page.Items.Count;
            foreach (var item in page.Items)
            {
                onItem(item);
            }

            if (page.Items.Count < pageSize
                || (stopAtTotal && page.TotalRecordCount.HasValue && startIndex + page.Items.Count >= page.TotalRecordCount.Value))
            {
                break;
            }

            startIndex += page.Items.Count;
        }

        if (describeShift is not null && expectedTotal.HasValue && rawFetched != expectedTotal.Value)
        {
            throw new InvalidOperationException(describeShift(expectedTotal.Value, rawFetched));
        }

        return rawFetched;
    }
}
