using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Service for synchronizing the people sync table with source and local servers.
/// Matches people by name (unique across servers).
/// </summary>
public class PeopleSyncTableService
{
    private readonly ILogger<PeopleSyncTableService> _logger;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeopleSyncTableService"/> class.
    /// </summary>
    public PeopleSyncTableService(
        ILogger<PeopleSyncTableService> logger,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Refreshes the people sync table by fetching all people from the source server
    /// and comparing with local people by name.
    /// </summary>
    /// <param name="client">Source server client.</param>
    /// <param name="database">Sync database.</param>
    /// <param name="syncImages">Whether to sync images.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="onItemProcessed">Optional callback after each person is processed.</param>
    /// <returns>Number of people processed.</returns>
    public async Task<int> RefreshPeopleSyncTableAsync(
        SourceServerClient client,
        SyncDatabase database,
        bool syncImages,
        CancellationToken cancellationToken,
        Action? onItemProcessed = null)
    {
        _logger.LogInformation("Refreshing people sync table");

        // Load existing people sync items into a lookup by name
        var existingItems = new Dictionary<string, PeopleSyncItem>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var item in database.GetAllPeopleSyncItems())
            {
                existingItems[item.PersonName] = item;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load existing people sync items");
            return 0;
        }

        // Build a lookup of all local people by name for matching
        var localPeopleLookup = BuildLocalPeopleLookup();

        // Fetch all source persons page by page
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processedCount = 0;
        var startIndex = 0;
        const int PageSize = 100;

        while (!cancellationToken.IsCancellationRequested)
        {
            using var doc = await client.GetPersonsAsync(startIndex, PageSize, cancellationToken).ConfigureAwait(false);
            if (doc == null)
            {
                _logger.LogWarning("Failed to fetch persons page at index {StartIndex}", startIndex);
                break;
            }

            if (!doc.RootElement.TryGetProperty("Items", out var itemsArray))
            {
                break;
            }

            var pageCount = 0;
            foreach (var personElement in itemsArray.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var name = personElement.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                seenNames.Add(name);
                pageCount++;

                ProcessSourcePerson(personElement, name, existingItems, localPeopleLookup, syncImages, client, database);

                processedCount++;
                onItemProcessed?.Invoke();
            }

            // Check if we've fetched all pages
            var totalRecordCount = doc.RootElement.TryGetProperty("TotalRecordCount", out var totalProp)
                ? totalProp.GetInt32()
                : 0;

            startIndex += pageCount;
            if (startIndex >= totalRecordCount || pageCount == 0)
            {
                break;
            }
        }

        // Remove stale records for people no longer on source
        var staleCount = 0;
        foreach (var kvp in existingItems)
        {
            if (!seenNames.Contains(kvp.Key))
            {
                database.DeletePeopleSyncItemByName(kvp.Key);
                staleCount++;
                _logger.LogDebug("Removed stale people record for {PersonName} (no longer on source)", kvp.Key);
            }
        }

        if (staleCount > 0)
        {
            _logger.LogInformation("Removed {Count} stale people sync records", staleCount);
        }

        _logger.LogInformation("Processed {Count} people from source server", processedCount);
        return processedCount;
    }

    /// <summary>
    /// Gets the total count of persons on the source server.
    /// </summary>
    public async Task<int> GetTotalPersonCountAsync(
        SourceServerClient client,
        CancellationToken cancellationToken)
    {
        return await client.GetPersonCountAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a lookup of local people (Person entities) by name.
    /// Uses ILibraryManager to query for all Person-type items.
    /// </summary>
    private Dictionary<string, MediaBrowser.Controller.Entities.BaseItem> BuildLocalPeopleLookup()
    {
        var lookup = new Dictionary<string, MediaBrowser.Controller.Entities.BaseItem>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var query = new MediaBrowser.Controller.Entities.InternalPeopleQuery();
            var localPeople = _libraryManager.GetPeopleNames(query);

            foreach (var personName in localPeople)
            {
                if (string.IsNullOrEmpty(personName))
                {
                    continue;
                }

                // GetPerson returns the Person entity by name
                var personItem = _libraryManager.GetPerson(personName);
                if (personItem != null)
                {
                    lookup[personName] = personItem;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build local people lookup");
        }

        return lookup;
    }

    /// <summary>
    /// Processes a single source person and upserts to the sync table.
    /// </summary>
    private void ProcessSourcePerson(
        JsonElement personElement,
        string name,
        Dictionary<string, PeopleSyncItem> existingItems,
        Dictionary<string, MediaBrowser.Controller.Entities.BaseItem> localPeopleLookup,
        bool syncImages,
        SourceServerClient client,
        SyncDatabase database)
    {
        var sourcePersonId = personElement.TryGetProperty("Id", out var idProp) ? idProp.GetString() : null;
        var sourceOverview = personElement.TryGetProperty("Overview", out var overviewProp) ? overviewProp.GetString() : null;

        // Extract provider IDs
        string? sourceProviderIds = null;
        if (personElement.TryGetProperty("ProviderIds", out var providerIdsProp) && providerIdsProp.ValueKind == JsonValueKind.Object)
        {
            var providerDict = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in providerIdsProp.EnumerateObject())
            {
                var val = prop.Value.GetString();
                if (!string.IsNullOrEmpty(val))
                {
                    providerDict[prop.Name] = val;
                }
            }

            if (providerDict.Count > 0)
            {
                sourceProviderIds = JsonSerializer.Serialize(providerDict);
            }
        }

        // Find local person by name
        localPeopleLookup.TryGetValue(name, out var localPerson);
        var localPersonId = localPerson?.Id.ToString("N", CultureInfo.InvariantCulture);

        // Extract local overview and provider IDs if local person exists
        string? localOverview = null;
        string? localProviderIds = null;

        if (localPerson != null)
        {
            localOverview = localPerson.Overview;

            if (localPerson.ProviderIds != null && localPerson.ProviderIds.Count > 0)
            {
                var localDict = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in localPerson.ProviderIds)
                {
                    if (!string.IsNullOrEmpty(kvp.Value))
                    {
                        localDict[kvp.Key] = kvp.Value;
                    }
                }

                if (localDict.Count > 0)
                {
                    localProviderIds = JsonSerializer.Serialize(localDict);
                }
            }
        }

        // Build or update the sync item
        var existingItem = existingItems.GetValueOrDefault(name);

        if (existingItem != null)
        {
            // Update existing
            existingItem.SourcePersonId = sourcePersonId;
            existingItem.LocalPersonId = localPersonId;
            existingItem.SourceOverview = sourceOverview;
            existingItem.LocalOverview = localOverview;
            existingItem.SourceProviderIds = sourceProviderIds;
            existingItem.LocalProviderIds = localProviderIds;

            // Preserve Ignored status
            if (existingItem.Status != BaseSyncStatus.Ignored)
            {
                if (localPerson == null)
                {
                    // No local match — can't sync
                    existingItem.Status = BaseSyncStatus.Queued;
                    existingItem.StatusDate = DateTime.UtcNow;
                }
                else if (existingItem.HasChanges)
                {
                    existingItem.Status = BaseSyncStatus.Queued;
                    existingItem.StatusDate = DateTime.UtcNow;
                }
                else if (existingItem.Status == BaseSyncStatus.Queued)
                {
                    existingItem.Status = BaseSyncStatus.Synced;
                    existingItem.StatusDate = DateTime.UtcNow;
                    existingItem.LastSyncTime = DateTime.UtcNow;
                }
            }

            database.UpsertPeopleSyncItem(existingItem);
        }
        else
        {
            // Create new
            var newItem = new PeopleSyncItem
            {
                PersonName = name,
                SourcePersonId = sourcePersonId,
                LocalPersonId = localPersonId,
                SourceOverview = sourceOverview,
                LocalOverview = localOverview,
                SourceProviderIds = sourceProviderIds,
                LocalProviderIds = localProviderIds,
                StatusDate = DateTime.UtcNow
            };

            if (localPerson == null)
            {
                newItem.Status = BaseSyncStatus.Queued;
            }
            else if (newItem.HasChanges)
            {
                newItem.Status = BaseSyncStatus.Queued;
            }
            else
            {
                newItem.Status = BaseSyncStatus.Synced;
                newItem.LastSyncTime = DateTime.UtcNow;
            }

            database.UpsertPeopleSyncItem(newItem);
        }
    }
}
