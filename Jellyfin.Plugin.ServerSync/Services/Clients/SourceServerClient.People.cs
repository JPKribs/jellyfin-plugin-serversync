using System;
using System.Collections.Generic;
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
    /// <summary>
    /// Reads every person from the source in one unpaged <c>/Persons</c> call, the only route that answers in full for every API Key.
    /// It is sent once with the client's whole time limit, since a large source takes minutes to start the answer.
    /// Throws on any failure, since a partial list would make the People scan prune rows for people it never saw.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every person the source holds, with the fields the People scan compares, lock state included.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the source gave no answer.</exception>
    public async Task<IReadOnlyList<BaseItemDto>> GetAllPersonsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            var result = await client.Persons.GetAsync(
                config =>
                {
                    config.QueryParameters.Fields = new[]
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
}
