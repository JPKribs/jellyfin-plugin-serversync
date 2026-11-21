using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Sdk.Generated.Models;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Clients;

/// <summary>
/// Library listings, item lookups, file and companion downloads, and the shared paging loop.
/// </summary>
public partial class SourceServerClient
{
    /// <summary>
    /// Lists the media files one whitelisted item stands for, the item itself or every file under it. It walks
    /// one folder at a time, since a recursive query by parent returns nothing for a series.
    /// </summary>
    /// <param name="whitelistedId">The source id of the whitelisted item.</param>
    /// <param name="playlistUserId">The source user a playlist is read as, or null for any other item.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The leaf items the whitelisted item stands for.</returns>
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

        // Every failure rethrows, since an empty answer would read as files removed.
        var rootPage = await GetApiClient().Items.GetAsync(
            config =>
            {
                config.QueryParameters.Ids = new Guid?[] { whitelistedId };
                config.QueryParameters.Fields = new[] { ItemFields.Path, ItemFields.DateCreated, ItemFields.MediaSources };
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        rootItem = rootPage?.Items?.FirstOrDefault();

        // An entry gone from the source fails, so its files are not read as removed.
        if (rootItem == null || !rootItem.Id.HasValue)
        {
            throw new InvalidOperationException(
                $"Whitelist entry {whitelistedId} no longer exists on the source server, remove it from the mapping's whitelist to resume pruning");
        }

        if (leafTypes.Contains(rootItem.Type))
        {
            result.Add(rootItem);
            await ApplyPeerLinksAsync(result, cancellationToken).ConfigureAwait(false);
            return result;
        }

        var queue = new Queue<Guid>();

        // A playlist does not answer child queries, so its members seed the walk.
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
                    // Every failure rethrows, since a list cut short would read as files removed.
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
                },
                1000,
                child =>
                {
                    if (!child.Id.HasValue)
                    {
                        return;
                    }

                    // A repeat within one folder means the pages shifted and an item was skipped.
                    if (!parentSeen.Add(child.Id.Value))
                    {
                        throw new InvalidOperationException(
                            $"Whitelist children of {parentId} changed during enumeration (duplicate item across pages)");
                    }

                    // A collection's children may rightly repeat across whitelist entries.
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
                        queue.Enqueue(child.Id.Value);
                    }
                },
                PageEnd.NullOrEmptyEnds,
                (expected, fetched) => $"Whitelist children of {parentId} changed during enumeration (expected {expected}, fetched {fetched})",
                cancellationToken).ConfigureAwait(false);
        }

        await ApplyPeerLinksAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Reads one page of a library's media files. The order is stable on ties, and callers compare the
    /// reported total with what they fetched to catch a library that changed during the scan.
    /// </summary>
    /// <param name="libraryId">The source library to list.</param>
    /// <param name="startIndex">The index of the page's first item.</param>
    /// <param name="limit">The page size.</param>
    /// <param name="changedSince">Only items saved since this time, or null for all.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The page, or null when it could not be read for a reason other than an HTTP error.</returns>
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
            return await WithPeerLinksAsync(await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video };
                    config.QueryParameters.Fields = new[] { ItemFields.Path, ItemFields.DateCreated, ItemFields.MediaSources };
                    config.QueryParameters.MinDateLastSaved = changedSince.HasValue ? new DateTimeOffset(JPKribs.Jellyfin.Base.UtcTime.AsUtc(changedSince.Value)) : null;
                    config.QueryParameters.SortBy = new[] { ItemSortBy.DateCreated, ItemSortBy.SortName, ItemSortBy.IndexNumber };
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not Microsoft.Kiota.Abstractions.ApiException { ResponseStatusCode: >= 400 })
        {
            _logger.LogError(ex, "Failed to get items from library {LibraryId}", libraryId);
            return null;
        }
    }

    /// <summary>
    /// Lists the source server's collections for the Library filter picker. Collections live outside libraries,
    /// so the whole server is searched.
    /// </summary>
    /// <param name="searchTerm">Text the collection names must match, or null for all.</param>
    /// <param name="startIndex">The index of the page's first collection.</param>
    /// <param name="limit">The page size.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The page of collections, or null when it could not be read.</returns>
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to list collections from source server");
            return null;
        }
    }

    /// <summary>
    /// Reads a playlist's members, throwing on any error or change during the walk, since a list cut short
    /// would read as items removed from the source.
    /// </summary>
    private async Task<List<BaseItemDto>> GetPlaylistItemsAsync(Guid playlistId, Guid? userId, CancellationToken cancellationToken, bool distinct = true)
    {
        var members = new List<BaseItemDto>();
        var memberIds = new HashSet<Guid>();
        var client = GetApiClient();
        await PageThroughAsync(
            async (startIndex, pageSize, ct) =>
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
            },
            1000,
            item =>
            {
                // A playlist may hold an item twice, which is not a shifted page.
                if (item.Id.HasValue && (!distinct || memberIds.Add(item.Id.Value)))
                {
                    members.Add(item);
                }
            },
            PageEnd.NullOrEmptyEnds,
            (expected, fetched) => $"Playlist {playlistId} changed during enumeration (expected {expected} members, fetched {fetched})",
            cancellationToken,
            stopAtTotal: false).ConfigureAwait(false);

        await ApplyPeerLinksAsync(members, cancellationToken).ConfigureAwait(false);
        return members;
    }

    /// <summary>
    /// Lists the source server's playlists for the Library filter picker. Playlists live outside libraries,
    /// so the whole server is searched.
    /// </summary>
    /// <param name="searchTerm">Text the playlist names must match, or null for all.</param>
    /// <param name="startIndex">The index of the page's first playlist.</param>
    /// <param name="limit">The page size.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The page of playlists, or null when it could not be read.</returns>
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to list playlists from source server");
            return null;
        }
    }

    /// <summary>
    /// Lists a library's top level items, such as series, films, and albums, for the Library filter picker.
    /// </summary>
    /// <param name="libraryId">The source library to browse.</param>
    /// <param name="searchTerm">Text the item names must match, or null to browse.</param>
    /// <param name="startIndex">The index of the page's first item.</param>
    /// <param name="limit">The page size.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The page of items, or null when it could not be read.</returns>
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

                    // Search only works recursively, so it is limited to top level types.
                    if (!string.IsNullOrWhiteSpace(searchTerm))
                    {
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to get top-level items from library {LibraryId}", libraryId);
            return null;
        }
    }

    /// <summary>
    /// Counts a library's media files without reading them.
    /// </summary>
    /// <param name="libraryId">The source library to count.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The count, or 0 when it could not be read for a reason other than an HTTP error.</returns>
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
        catch (Exception ex) when (ex is not OperationCanceledException and not Microsoft.Kiota.Abstractions.ApiException { ResponseStatusCode: >= 400 })
        {
            _logger.LogWarning(ex, "Failed to get item count for library {LibraryId}", libraryId);
            return 0;
        }
    }

    /// <summary>
    /// Reads one page of a library's item ids and paths, a cheap listing a scan uses to find what this server
    /// holds. Every failure throws, since a null read as the end would make later items look removed.
    /// </summary>
    /// <param name="libraryId">The source library to list.</param>
    /// <param name="includeTypes">The item kinds to list.</param>
    /// <param name="startIndex">The index of the page's first item.</param>
    /// <param name="limit">The page size.</param>
    /// <param name="changedSince">Only items saved since this time, or null for all.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
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
        ArgumentNullException.ThrowIfNull(includeTypes);

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
                    config.QueryParameters.MinDateLastSaved = changedSince.HasValue ? new DateTimeOffset(JPKribs.Jellyfin.Base.UtcTime.AsUtc(changedSince.Value)) : null;
                    config.QueryParameters.SortBy = new[] { ItemSortBy.DateCreated, ItemSortBy.SortName, ItemSortBy.IndexNumber };
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not Microsoft.Kiota.Abstractions.ApiException { ResponseStatusCode: >= 400 })
        {
            throw new InvalidOperationException($"Failed to get item paths from library {libraryId} at index {startIndex}: {ex.Message}", ex);
        }

        return await WithPeerLinksAsync(page, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Source server returned no page for library {libraryId} at index {startIndex}");
    }

    /// <summary>
    /// Reads full details for a set of items in batches. Every failure throws, since a partial answer would
    /// make the missing items look removed from the source.
    /// </summary>
    /// <param name="ids">The source item ids to fetch.</param>
    /// <param name="fields">The fields to read for each item.</param>
    /// <param name="batchSize">How many ids go in one request.</param>
    /// <param name="cancellationToken">Cancels the fetch between and during batches.</param>
    /// <returns>The items the source returned.</returns>
    public async Task<List<BaseItemDto>> GetItemsByIdsAsync(
        IReadOnlyList<Guid> ids,
        ItemFields[] fields,
        int batchSize = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(fields);

        var result = new List<BaseItemDto>();
        if (ids.Count == 0)
        {
            return result;
        }

        for (var i = 0; i < ids.Count; i += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = ids.Skip(i).Take(batchSize).Cast<Guid?>().ToArray();

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
                await ApplyPeerLinksAsync(response.Items, cancellationToken).ConfigureAwait(false);
                result.AddRange(response.Items);
            }
        }

        return result;
    }

    /// <summary>
    /// Reads one item's details, throwing when the server cannot be asked. Null means only that the server
    /// holds no such item, so a caller can tell a gone item from an unreachable one.
    /// </summary>
    /// <param name="itemId">The source id of the item to read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The item, or null when the server holds none by that id.</returns>
    public async Task<BaseItemDto?> FindItemDetailsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var client = GetApiClient();
        var result = await client.Items.GetAsync(
            config =>
            {
                config.QueryParameters.Ids = new Guid?[] { itemId };
                config.QueryParameters.Fields = new[] { ItemFields.Path, ItemFields.DateCreated, ItemFields.MediaSources, ItemFields.MediaStreams, ItemFields.ProviderIds };
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await ApplyPeerLinksAsync(result?.Items, cancellationToken).ConfigureAwait(false);
        return result?.Items?.FirstOrDefault();
    }

    /// <summary>
    /// Downloads an item's file from the source server, from its first byte.
    /// </summary>
    /// <param name="itemId">The source id of the item whose file is downloaded.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The file's content, and its length when the server gave one.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the download could not start, with the cause in the message.</exception>
    public async Task<(Stream Stream, long? ContentLength)> DownloadFileAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var download = await DownloadFileFromAsync(itemId, 0, null, cancellationToken).ConfigureAwait(false);
        return (download.Stream, download.ContentLength);
    }

    /// <summary>
    /// Downloads an item's file from the source server, from a byte offset when the source can resume it. The
    /// range is sent with the earlier answer's validator, so a file that changed since then comes back whole.
    /// </summary>
    /// <param name="itemId">The source id of the item whose file is downloaded.</param>
    /// <param name="from">The byte to start from, 0 for the whole file.</param>
    /// <param name="validator">The validator from the earlier answer, or null when there was none.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The download, saying where its bytes start.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the download could not start, with the cause in the message.</exception>
    public async Task<SourceDownload> DownloadFileFromAsync(Guid itemId, long from, string? validator, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage? response = null;
        HttpRequestMessage? request = null;
        var ownershipTransferred = false;
        try
        {
            var url = $"{_serverUrl}/Items/{itemId}/Download";
            RangeConditionHeaderValue? ifRange = null;
            var resume = from > 0 && SourceDownload.TryParseIfRange(validator, out ifRange);
            request = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuthorizationHeader(request);
            if (resume)
            {
                request.Headers.Range = new RangeHeaderValue(from, null);
                request.Headers.IfRange = ifRange;
            }

            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            // The file is shorter than the bytes kept, so it changed and starts over.
            if (resume && response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
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
            var wrapper = new ResponseDisposingStream(stream, response, request);
            ownershipTransferred = true;
            return new SourceDownload(wrapper, contentLength, total, offset, SourceDownload.ValidatorOf(response));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
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
    /// Lists an item's external subtitle files, the only external streams Jellyfin can serve for download. A
    /// failure to read the item throws, so an empty list always means the source has none.
    /// </summary>
    /// <param name="itemId">The source id of the item whose subtitles are listed.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The subtitle files, empty when the item has none or the source holds no such item.</returns>
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

            // No route serves external audio, and asking for one returns the main file.
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
            _logger.LogDebug(
                "Item {ItemId} has {Count} external non-subtitle stream(s) (e.g. audio) that cannot be downloaded via the API. Skipping",
                itemId, skippedNonSubtitle);
        }

        return companions;
    }

    /// <summary>
    /// Downloads an external subtitle in its own format through the subtitle stream route. Routes that take a
    /// path serve the item's main file instead, which would write video into the subtitle.
    /// </summary>
    /// <param name="itemId">The source id of the item the subtitle belongs to.</param>
    /// <param name="companion">The subtitle stream to download.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The subtitle's content, or null when it could not be downloaded.</returns>
    public async Task<Stream?> DownloadCompanionFileAsync(Guid itemId, CompanionFileInfo companion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(companion);
        HttpResponseMessage? response = null;
        HttpRequestMessage? request = null;
        var ownershipTransferred = false;
        try
        {
            var format = Path.GetExtension(companion.SourcePath).TrimStart('.').ToLowerInvariant();

            // A path without an extension falls back to the codec, so nothing is converted.
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
            var wrapper = new ResponseDisposingStream(stream, response, request);
            ownershipTransferred = true;
            return wrapper;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to download companion subtitle {FilePath} (stream {Index}) for {ItemId}", FormatUtilities.SanitizeForLog(companion.SourcePath), companion.StreamIndex, itemId);
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
    /// Walks a paged listing until an empty page, a short page, or the reported total. With
    /// <paramref name="describeShift"/> set, it throws when the fetched count differs from the first total,
    /// which means the listing changed and an item may have been skipped.
    /// </summary>
    /// <param name="fetchPage">Fetches one page from a start index and size, and owns its error handling.</param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="onItem">Handles each item, in order. It may throw to abort the walk.</param>
    /// <param name="end">How an answer with no items is read.</param>
    /// <param name="describeShift">Describes a changed listing from the expected and fetched counts, or null to skip the check.</param>
    /// <param name="cancellationToken">Cancels the walk between pages.</param>
    /// <param name="stopAtTotal">Whether reaching the reported total ends the walk before a short page does.</param>
    /// <returns>A task that completes when the walk ends.</returns>
    private static async Task PageThroughAsync(
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
    }
}
