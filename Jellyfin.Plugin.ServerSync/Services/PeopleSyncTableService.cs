using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Service for synchronizing the people sync table with source and local servers.
/// Derives the person list from synced metadata items rather than the global /Persons endpoint.
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
    /// Refreshes the people sync table by extracting unique person names from synced metadata items,
    /// then fetching each person individually from the source server.
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
        _logger.LogInformation("Refreshing people sync table from synced metadata items");

        // Step 1: Extract unique person names from all synced metadata items
        var uniqueNames = ExtractUniquePersonNames(database);

        if (uniqueNames.Count == 0)
        {
            _logger.LogInformation("No people found in synced metadata items. Run a metadata refresh first");
            return 0;
        }

        _logger.LogInformation("Found {Count} unique people across synced metadata items", uniqueNames.Count);

        // Step 2: Load existing people sync items into a lookup by name
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

        // Step 3: For each unique person, fetch from source and compare with local
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processedCount = 0;

        foreach (var name in uniqueNames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            seenNames.Add(name);

            await ProcessPersonAsync(name, client, database, existingItems, syncImages, cancellationToken).ConfigureAwait(false);

            processedCount++;
            onItemProcessed?.Invoke();
        }

        // Step 4: Remove stale records for people no longer in any synced item
        var staleCount = 0;
        foreach (var kvp in existingItems)
        {
            if (!seenNames.Contains(kvp.Key))
            {
                database.DeletePeopleSyncItemByName(kvp.Key);
                staleCount++;
                _logger.LogDebug("Removed stale people record for {PersonName} (no longer in synced items)", kvp.Key);
            }
        }

        if (staleCount > 0)
        {
            _logger.LogInformation("Removed {Count} stale people sync records", staleCount);
        }

        _logger.LogInformation("Processed {Count} people from synced metadata items", processedCount);
        return processedCount;
    }

    /// <summary>
    /// Populates source and local image data for a person.
    /// Returns (sourceImagesValue, sourceImagesHash, localImagesValue).
    /// </summary>
    private async Task<(string? SourceImagesValue, string? SourceImagesHash, string? LocalImagesValue)> PopulateImageDataAsync(
        string? sourcePersonId,
        MediaBrowser.Controller.Entities.BaseItem? localPerson,
        SourceServerClient client,
        CancellationToken cancellationToken)
    {
        string? sourceImagesValue = null;
        string? sourceImagesHash = null;
        string? localImagesValue = null;

        // Source images - fetch via API using sourcePersonId
        if (!string.IsNullOrEmpty(sourcePersonId) && Guid.TryParse(sourcePersonId, out var sourceGuid))
        {
            try
            {
                var imageInfoList = await client.GetItemImageInfoAsync(sourceGuid, cancellationToken).ConfigureAwait(false);
                if (imageInfoList != null && imageInfoList.Count > 0)
                {
                    var sourceImagesByType = new Dictionary<string, List<ImageInfoDto>>();
                    foreach (var img in imageInfoList)
                    {
                        var imageTypeName = img.ImageType?.ToString() ?? "Unknown";
                        if (!sourceImagesByType.TryGetValue(imageTypeName, out var imageList))
                        {
                            imageList = new List<ImageInfoDto>();
                            sourceImagesByType[imageTypeName] = imageList;
                        }

                        imageList.Add(new ImageInfoDto
                        {
                            ImageType = imageTypeName,
                            ImageIndex = img.ImageIndex ?? 0,
                            Size = img.Size ?? 0,
                            Width = img.Width ?? 0,
                            Height = img.Height ?? 0,
                            Tag = img.ImageTag
                        });
                    }

                    if (sourceImagesByType.Count > 0)
                    {
                        sourceImagesValue = JsonSerializer.Serialize(sourceImagesByType);

                        var hashInput = string.Join(";", sourceImagesByType
                            .OrderBy(k => k.Key)
                            .Select(k => $"{k.Key}:{string.Join(",", k.Value.Select(v => $"{v.Size}_{v.Width}x{v.Height}"))}"));
                        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
                        sourceImagesHash = Convert.ToHexString(hashBytes).ToLowerInvariant()[..32];
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch image info for person {PersonName}", sourcePersonId);
            }
        }

        // Local images - people typically only have Primary images
        if (localPerson != null)
        {
            var localImagesByType = new Dictionary<string, List<ImageInfoDto>>();
            var images = localPerson.GetImages(ImageType.Primary).ToList();
            if (images.Count > 0)
            {
                var imageInfoList = new List<ImageInfoDto>();
                for (int idx = 0; idx < images.Count; idx++)
                {
                    var img = images[idx];
                    long fileSize = 0;

                    if (!string.IsNullOrEmpty(img.Path) && File.Exists(img.Path))
                    {
                        try
                        {
                            fileSize = new FileInfo(img.Path).Length;
                        }
                        catch (IOException)
                        {
                            // Ignore file access errors
                        }
                    }

                    imageInfoList.Add(new ImageInfoDto
                    {
                        ImageType = ImageType.Primary.ToString(),
                        ImageIndex = idx,
                        Size = fileSize,
                        Width = img.Width,
                        Height = img.Height
                    });
                }

                localImagesByType[ImageType.Primary.ToString()] = imageInfoList;
            }

            if (localImagesByType.Count > 0)
            {
                localImagesValue = JsonSerializer.Serialize(localImagesByType);
            }
        }

        return (sourceImagesValue, sourceImagesHash, localImagesValue);
    }

    /// <summary>
    /// Extracts unique person names from all SourcePeopleValue JSON in the metadata sync table.
    /// </summary>
    public HashSet<string> ExtractUniquePersonNames(SyncDatabase database)
    {
        var uniqueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var allPeopleJsons = database.GetAllSourcePeopleValues();

        foreach (var jsonStr in allPeopleJsons)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonStr);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var personElement in doc.RootElement.EnumerateArray())
                {
                    if (personElement.TryGetProperty("Name", out var nameProp))
                    {
                        var name = nameProp.GetString();
                        if (!string.IsNullOrEmpty(name))
                        {
                            uniqueNames.Add(name);
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse people JSON from metadata sync item");
            }
        }

        return uniqueNames;
    }

    /// <summary>
    /// Processes a single person: fetches from source server, compares with local, and upserts.
    /// </summary>
    private async Task ProcessPersonAsync(
        string name,
        SourceServerClient client,
        SyncDatabase database,
        Dictionary<string, PeopleSyncItem> existingItems,
        bool syncImages,
        CancellationToken cancellationToken)
    {
        // Fetch person details from source server
        string? sourcePersonId = null;
        string? sourceOverview = null;
        string? sourceProviderIds = null;

        using var doc = await client.GetPersonByNameAsync(name, cancellationToken).ConfigureAwait(false);
        if (doc != null)
        {
            sourcePersonId = doc.RootElement.TryGetProperty("Id", out var idProp) ? idProp.GetString() : null;
            sourceOverview = doc.RootElement.TryGetProperty("Overview", out var overviewProp) ? overviewProp.GetString() : null;

            if (doc.RootElement.TryGetProperty("ProviderIds", out var providerIdsProp) && providerIdsProp.ValueKind == JsonValueKind.Object)
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
        }

        // Find local person by name, then get the fully-loaded entity via GetItemById.
        // GetPerson returns an incomplete Person (missing Overview, etc.); GetItemById
        // retrieves the full BaseItem from the database.
        var personStub = _libraryManager.GetPerson(name);
        var localPerson = personStub != null ? _libraryManager.GetItemById(personStub.Id) : null;
        var localPersonId = localPerson?.Id.ToString("N", CultureInfo.InvariantCulture);

        // Extract local overview and provider IDs from the fully-loaded entity
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

        // Populate image comparison data when syncImages is enabled
        string? sourceImagesValue = null;
        string? sourceImagesHash = null;
        string? localImagesValue = null;

        if (syncImages)
        {
            (sourceImagesValue, sourceImagesHash, localImagesValue) = await PopulateImageDataAsync(
                sourcePersonId, localPerson, client, cancellationToken).ConfigureAwait(false);
        }

        // Build or update the sync item
        var existingItem = existingItems.GetValueOrDefault(name);

        if (existingItem != null)
        {
            existingItem.SourcePersonId = sourcePersonId;
            existingItem.LocalPersonId = localPersonId;
            existingItem.SourceOverview = sourceOverview;
            existingItem.LocalOverview = localOverview;
            existingItem.SourceProviderIds = sourceProviderIds;
            existingItem.LocalProviderIds = localProviderIds;
            existingItem.SourceImagesValue = sourceImagesValue;
            existingItem.LocalImagesValue = localImagesValue;
            existingItem.SourceImagesHash = sourceImagesHash;

            if (existingItem.Status != BaseSyncStatus.Ignored)
            {
                if (existingItem.HasChanges)
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
            var newItem = new PeopleSyncItem
            {
                PersonName = name,
                SourcePersonId = sourcePersonId,
                LocalPersonId = localPersonId,
                SourceOverview = sourceOverview,
                LocalOverview = localOverview,
                SourceProviderIds = sourceProviderIds,
                LocalProviderIds = localProviderIds,
                SourceImagesValue = sourceImagesValue,
                LocalImagesValue = localImagesValue,
                SourceImagesHash = sourceImagesHash,
                StatusDate = DateTime.UtcNow
            };

            if (newItem.HasChanges)
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
