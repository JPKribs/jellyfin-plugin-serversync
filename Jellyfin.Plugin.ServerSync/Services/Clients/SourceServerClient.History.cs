using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Clients;

/// <summary>
/// Reads History from the source, as changed items, played items, and items with their user data.
/// </summary>
public partial class SourceServerClient
{
    /// <summary>
    /// Lists the items in a library whose user data changed for a user since a time.
    /// </summary>
    /// <param name="userId">The source user whose changes are listed.</param>
    /// <param name="libraryId">The source library to look in.</param>
    /// <param name="includeTypes">The item kinds to list.</param>
    /// <param name="changedSince">The time after which a change counts.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The ids of the items changed since then.</returns>
    public async Task<HashSet<Guid>> GetItemIdsChangedForUserAsync(Guid userId, Guid libraryId, BaseItemKind[] includeTypes, DateTime changedSince, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(includeTypes);

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
                catch (Exception ex) when (ex is not OperationCanceledException)
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
    /// Reads a batch of source items by id with one user's history, so a scan asks only for items this server holds.
    /// </summary>
    /// <param name="userId">The source user whose user data is read.</param>
    /// <param name="ids">The source item ids to fetch.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The items with their user data, or null when the source gave no answer.</returns>
    /// <exception cref="Microsoft.Kiota.Abstractions.ApiException">Thrown when the source answers with an error status, so the scan stops before it prunes.</exception>
    public async Task<BaseItemDtoQueryResult?> GetItemsWithUserDataByIdsAsync(
        Guid userId,
        Guid?[] ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Length == 0)
        {
            return null;
        }

        try
        {
            var client = GetApiClient();
            return await WithPeerLinksAsync(await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.UserId = userId;
                    config.QueryParameters.Ids = ids;
                    config.QueryParameters.EnableUserData = true;
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.Path,
                        ItemFields.DateCreated
                    };
                },
                cancellationToken: cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
        {
            _logger.LogWarning(ex, "Source server {Status} fetching user-data items for user {UserId}. Aborting refresh to protect tracking rows", ex.ResponseStatusCode, userId);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch user-data items for user {UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// Lists the id of every item a source user has played in a library, throwing on a failed page so a failure never reads as nothing played.
    /// </summary>
    /// <param name="userId">The source user whose plays are listed.</param>
    /// <param name="libraryId">The source library to look in.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The ids of the played items.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a page could not be read.</exception>
    public async Task<HashSet<Guid>> GetUserPlayedItemIdsAsync(
        Guid userId,
        Guid libraryId,
        CancellationToken cancellationToken = default)
    {
        var ids = new HashSet<Guid>();
        await PageThroughAsync(
            async (startIndex, pageSize, ct) =>
                await GetUserPlayedItemsAsync(userId, libraryId, startIndex, pageSize, ct).ConfigureAwait(false)
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
    /// Reads one page of the items a source user has played in a library, with their user data.
    /// </summary>
    /// <param name="userId">The source user whose plays are listed.</param>
    /// <param name="libraryId">The source library to look in.</param>
    /// <param name="startIndex">The index of the first item of the page.</param>
    /// <param name="limit">The most items the page may hold.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The page, or null when the request failed without an error status.</returns>
    /// <exception cref="Microsoft.Kiota.Abstractions.ApiException">Thrown when the source answers with an error status, so the scan stops before it prunes.</exception>
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
            return await WithPeerLinksAsync(await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.UserId = userId;
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video };
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.Path,
                        ItemFields.DateCreated
                    };
                    config.QueryParameters.EnableUserData = true;
                    config.QueryParameters.IsPlayed = true;
                    config.QueryParameters.SortBy = new[] { ItemSortBy.DateCreated, ItemSortBy.SortName, ItemSortBy.IndexNumber };
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
        {
            _logger.LogWarning(ex, "Source server {Status} getting played items for user {UserId} in library {LibraryId}. Aborting refresh to protect tracking rows", ex.ResponseStatusCode, userId, libraryId);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to get played items for user {UserId} in library {LibraryId}", userId, libraryId);
            return null;
        }
    }
}
