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
/// People.
/// </summary>
public partial class SourceServerClient
{
    // ===== People Sync Methods =====

    /// <summary>
    /// Bulk-fetches every Person from the source server in a single
    /// <c>/Persons?fields=…</c> call so callers can do a local name-keyed
    /// join instead of one HTTP call per person. <c>/Persons</c> ignores
    /// <c>StartIndex</c> so the catalog cannot be paginated, but unlike
    /// <c>/Items?recursive=true</c> it is not scoped to the requesting
    /// user's libraries, Person items live outside library folders, so the
    /// Items route returns an empty 200 for non-admin tokens (which is how
    /// 10.11.64.0 wiped the People tracking table). Response can be tens of
    /// MB on libraries with 100k+ persons, but it is the only route that
    /// answers correctly for every token type. Throws on any failure: a
    /// partial catalog would make the People refresh prune rows for persons
    /// it never saw.
    /// </summary>
    /// <returns>All persons returned by the source server.</returns>
    public async Task<IReadOnlyList<BaseItemDto>> GetAllPersonsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            var result = await client.Persons.GetAsync(
                config =>
                {
                    // No searchTerm and no Limit, request the entire catalog.
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.Overview,
                        ItemFields.ProviderIds,
                        ItemFields.Tags,
                        ItemFields.OriginalTitle,
                        ItemFields.SortName,
                        ItemFields.DateCreated,
                        ItemFields.ProductionLocations,
                        // Settings populates LockData + LockedFields.
                        // without it those come back null while local has
                        // them populated, producing a perpetual false diff.
                        ItemFields.Settings
                    };
                },
                cancellationToken).ConfigureAwait(false);

            if (result?.Items == null)
            {
                // A null response is a failure, not an empty catalog , 
                // treating it as empty would prune every tracking row.
                throw new InvalidOperationException(
                    "Source server returned no response fetching the Persons catalog");
            }

            return result.Items;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
        {
            // Any 4xx/5xx isn't "source has no persons", it's "source didn't
            // give us a real answer." Returning empty here makes the People
            // refresh prune every local tracking row. Bubble up so the refresh
            // aborts before prune.
            _logger.LogWarning(ex, "Source server {Status} bulk-fetching Persons. Aborting refresh to protect tracking rows", ex.ResponseStatusCode);
            throw;
        }
        catch (Exception ex)
        {
            // Same reasoning as the 4xx/5xx case: a transport fault, timeout, or
            // deserialization error is "no real answer," not "source has no
            // persons." The People refresh has no incomplete-discovery prune
            // guard, so returning empty here would prune every tracking row.
            // Bubble up so the refresh aborts before prune.
            _logger.LogWarning(ex, "Failed to bulk-fetch Persons from source server. Aborting refresh to protect tracking rows");
            throw;
        }
    }
}
