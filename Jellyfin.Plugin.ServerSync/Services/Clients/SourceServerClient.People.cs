using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Clients;

/// <summary>
/// Reads People from the source.
/// </summary>
public partial class SourceServerClient
{
    /// <summary>How many people one page of the list asks for.</summary>
    public const int PersonPageSize = 2000;

    /// <summary>The fields the People scan compares, lock state included.</summary>
    private static readonly ItemFields[] PersonFields =
    {
        ItemFields.Overview,
        ItemFields.ProviderIds,
        ItemFields.Tags,
        ItemFields.OriginalTitle,
        ItemFields.SortName,
        ItemFields.DateCreated,
        ItemFields.ProductionLocations,
        ItemFields.Settings
    };

    /// <summary>
    /// Reads every person from the source, a page at a time from <c>/Items</c>, so no one request has to carry the whole catalog.
    /// A name the source holds twice is settled by asking the source for that name, which keeps the person <c>/Persons</c> lists.
    /// A source that lists nothing this way is asked once through the unpaged <c>/Persons</c> call, which an older source needs for a standard user's key.
    /// Throws on any failure, since a partial list would make the People scan prune rows for people it never saw.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every person the source holds, one for each name, with the fields the People scan compares.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the source gave no answer or its list changed while it was read.</exception>
    public async Task<IReadOnlyList<BaseItemDto>> GetAllPersonsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var paged = await GetPersonPagesAsync(cancellationToken).ConfigureAwait(false);
            return paged.Count > 0
                ? await KeepOnePersonForEachNameAsync(paged, cancellationToken).ConfigureAwait(false)
                : await GetPersonsUnpagedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
        {
            _logger.LogWarning(ex, "Source server {Status} bulk-fetching Persons. Aborting refresh to protect tracking rows", ex.ResponseStatusCode);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to bulk-fetch Persons from source server. Aborting refresh to protect tracking rows");
            throw;
        }
    }

    /// <summary>
    /// Walks the paged list of Person items. A person met twice means the list moved between pages, which fails the read.
    /// </summary>
    private async Task<List<BaseItemDto>> GetPersonPagesAsync(CancellationToken cancellationToken)
    {
        var client = GetApiClient();
        var people = new List<BaseItemDto>();
        var ids = new HashSet<Guid>();
        await PageThroughAsync(
            async (startIndex, pageSize, ct) =>
            {
                var page = await client.Items.GetAsync(
                    config =>
                    {
                        config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Person };
                        config.QueryParameters.Recursive = true;
                        config.QueryParameters.Fields = PersonFields;

                        // A fixed order, so a page boundary never repeats or skips a person.
                        config.QueryParameters.SortBy = new[] { ItemSortBy.SortName, ItemSortBy.DateCreated };
                        config.QueryParameters.StartIndex = startIndex;
                        config.QueryParameters.Limit = pageSize;
                    },
                    ct).ConfigureAwait(false);

                // A missing answer read as no people would prune every row.
                return page?.Items is null
                    ? throw new InvalidOperationException("Source server returned no response fetching the Persons catalog")
                    : page;
            },
            PersonPageSize,
            person =>
            {
                if (person.Id is { } id && !ids.Add(id))
                {
                    throw new InvalidOperationException("The source's list of people changed while it was read");
                }

                people.Add(person);
            },
            PageEnd.EmptyEnds,
            (expected, fetched) => $"The source's list of people changed while it was read, from {expected} to {fetched}",
            cancellationToken).ConfigureAwait(false);
        return people;
    }

    /// <summary>
    /// Keeps one person for each name. The source can hold two Person items under one name, and its own lookup by name says which one it uses.
    /// </summary>
    private async Task<IReadOnlyList<BaseItemDto>> KeepOnePersonForEachNameAsync(List<BaseItemDto> people, CancellationToken cancellationToken)
    {
        var doubled = people
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .GroupBy(p => p.Name!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();
        if (doubled.Count == 0)
        {
            return people;
        }

        var dropped = new HashSet<BaseItemDto>();
        foreach (var group in doubled)
        {
            var used = await GetPersonByNameAsync(group.Key, cancellationToken).ConfigureAwait(false);

            // With no answer for the name, the first one listed stands, as it would in the scan.
            var kept = group.FirstOrDefault(p => p.Id.HasValue && p.Id == used?.Id) ?? group.First();
            foreach (var person in group)
            {
                if (!ReferenceEquals(person, kept))
                {
                    dropped.Add(person);
                }
            }
        }

        _logger.LogDebug("The source holds {Count} names under more than one person, and its lookup by name settled each", doubled.Count);
        return people.Where(p => !dropped.Contains(p)).ToList();
    }

    /// <summary>
    /// Reads every person in one unpaged <c>/Persons</c> call, which is sent once and may take thirty minutes on a large source.
    /// </summary>
    private async Task<IReadOnlyList<BaseItemDto>> GetPersonsUnpagedAsync(CancellationToken cancellationToken)
    {
        var client = GetApiClient();
        var result = await client.Persons.GetAsync(
            config =>
            {
                config.QueryParameters.Fields = PersonFields;

                // A source with a large catalog takes minutes to start this answer.
                config.Options.Add(new SlowAnswerOption());
            },
            cancellationToken).ConfigureAwait(false);

        // A missing answer read as no people would prune every row.
        if (result?.Items == null)
        {
            throw new InvalidOperationException(
                "Source server returned no response fetching the Persons catalog");
        }

        return result.Items;
    }
}
