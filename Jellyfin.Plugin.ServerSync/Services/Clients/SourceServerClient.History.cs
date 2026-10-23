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
/// Watch history: changed and played items, and items with their user data.
/// </summary>
public partial class SourceServerClient
{
    // ===== History Sync Methods =====

    public async Task<HashSet<Guid>> GetItemIdsChangedForUserAsync(Guid userId, Guid libraryId, BaseItemKind[] includeTypes, DateTime changedSince, CancellationToken cancellationToken = default)
    {
        var ids = new HashSet<Guid>();
        var client = GetApiClient();
        await PageThroughAsync(
            async (startIndex, pageSize, ct) =>
            {
                BaseItemDtoQueryResult? page;
                try
                {
                    page = await client.Items.GetAsync(
                        config =>
                        {
                            config.QueryParameters.UserId = userId;
                            config.QueryParameters.ParentId = libraryId;
                            config.QueryParameters.Recursive = true;
                            config.QueryParameters.IncludeItemTypes = includeTypes;
                            config.QueryParameters.MinDateLastSavedForUser = new DateTimeOffset(Utilities.UtcTime.AsUtc(changedSince));
                            config.QueryParameters.EnableImages = false;
                            config.QueryParameters.EnableUserData = false;
                            config.QueryParameters.SortBy = new[] { ItemSortBy.DateCreated, ItemSortBy.SortName, ItemSortBy.IndexNumber };
                            config.QueryParameters.StartIndex = startIndex;
                            config.QueryParameters.Limit = pageSize;
                        },
                        cancellationToken: ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to list items changed for user {userId} in library {libraryId}: {ex.Message}", ex);
                }

                return page?.Items is null
                    ? throw new InvalidOperationException($"No answer listing items changed for user {userId} in library {libraryId}")
                    : page;
            },
            1000,
            item =>
            {
                if (item.Id.HasValue)
                {
                    ids.Add(item.Id.Value);
                }
            },
            PageEnd.EmptyEnds,
            null,
            cancellationToken).ConfigureAwait(false);
        return ids;
    }

    /// <summary>
    /// Batch-fetches items by ID with the specified user's UserData populated.
    /// Used by the History refresh task after the local-first discovery pass , 
    /// only items whose translated path exists locally get user-data fetched,
    /// rather than the prior approach of pulling every user-played item per
    /// (user × library) and discarding most on path mismatch.
    /// </summary>
    public async Task<BaseItemDtoQueryResult?> GetItemsWithUserDataByIdsAsync(
        Guid userId,
        Guid?[] ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Length == 0)
        {
            return null;
        }

        try
        {
            var client = GetApiClient();
            return await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.UserId = userId;
                    config.QueryParameters.Ids = ids;
                    config.QueryParameters.EnableUserData = true;
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.Path,
                        ItemFields.DateCreated,
                        ItemFields.MediaSources
                    };
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
        {
            // Any 4xx/5xx: bubble up so the History Refresh aborts before prune.
            _logger.LogWarning(ex, "Source server {Status} fetching user-data items for user {UserId}. Aborting refresh to protect tracking rows", ex.ResponseStatusCode, userId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch user-data items for user {UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// Fetches every item ID a user has played within a library, paginated.
    /// Throws on transient failure so callers don't conflate "error" with
    /// "user played nothing", a silent empty return would let downstream
    /// filters skip items they shouldn't.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the source server returns a null page (failure) rather
    /// than an explicit empty page.
    /// </exception>
    public async Task<HashSet<Guid>> GetUserPlayedItemIdsAsync(
        Guid userId,
        Guid libraryId,
        CancellationToken cancellationToken = default)
    {
        var ids = new HashSet<Guid>();
        await PageThroughAsync(
            async (startIndex, pageSize, ct) =>
                await GetUserPlayedItemsAsync(userId, libraryId, startIndex, pageSize, ct).ConfigureAwait(false)
                    // GetUserPlayedItemsAsync returns null on a logged transient
                    // failure. Treating that as end-of-list would silently classify
                    // every item as "user did not play this", wrong answer.
                    ?? throw new InvalidOperationException(
                        $"Failed to fetch played items page for user {userId} in library {libraryId} at index {startIndex}"),
            200,
            item =>
            {
                if (item.Id.HasValue)
                {
                    ids.Add(item.Id.Value);
                }
            },
            PageEnd.EmptyEnds,
            null,
            cancellationToken).ConfigureAwait(false);

        return ids;
    }

    /// <summary>
    /// Gets items that have been played by a specific user in a library.
    /// </summary>
    /// <param name="userId">User ID on the source server.</param>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="startIndex">Starting index for pagination.</param>
    /// <param name="limit">Maximum items to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with played items.</returns>
    public async Task<BaseItemDtoQueryResult?> GetUserPlayedItemsAsync(
        Guid userId,
        Guid libraryId,
        int startIndex = 0,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            return await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.UserId = userId;
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video };
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.Path,
                        ItemFields.DateCreated,
                        ItemFields.MediaSources
                    };
                    config.QueryParameters.EnableUserData = true;
                    config.QueryParameters.IsPlayed = true;
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
            // Any 4xx/5xx: bubble up so the History Refresh aborts before prune.
            // GetUserPlayedItemIdsAsync already throws on null page, so a rethrow
            // here propagates the same way.
            _logger.LogWarning(ex, "Source server {Status} getting played items for user {UserId} in library {LibraryId}. Aborting refresh to protect tracking rows", ex.ResponseStatusCode, userId, libraryId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get played items for user {UserId} in library {LibraryId}", userId, libraryId);
            return null;
        }
    }
}
