using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Scheduled task to apply queued people sync changes to local person entities.
/// </summary>
public class SyncMissingPeopleTask : IScheduledTask
{
    private readonly ILogger<SyncMissingPeopleTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISyncDatabaseProvider _databaseProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncMissingPeopleTask"/> class.
    /// </summary>
    public SyncMissingPeopleTask(
        ILogger<SyncMissingPeopleTask> logger,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        ISourceServerClientFactory clientFactory,
        IPluginConfigurationManager configManager,
        ISyncDatabaseProvider databaseProvider)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _clientFactory = clientFactory;
        _configManager = configManager;
        _databaseProvider = databaseProvider;
    }

    /// <inheritdoc />
    public string Name => "Sync People Data";

    /// <inheritdoc />
    public string Key => "ServerSyncMissingPeople";

    /// <inheritdoc />
    public string Description => "Applies queued people sync changes (metadata, images) to local person entities.";

    /// <inheritdoc />
    public string Category => "People Sync";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = _configManager.Configuration;

        if (!config.EnablePeopleSync)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(config.SourceServerUrl) || string.IsNullOrWhiteSpace(config.SourceServerApiKey))
        {
            _logger.LogWarning("People sync skipped: source server not configured");
            return;
        }

        var syncImages = config.PeopleSyncImages;

        _logger.LogInformation("Starting people sync");

        var database = _databaseProvider.Database;

        var queuedItems = database.GetPeopleSyncItemsByStatus(BaseSyncStatus.Queued);
        var totalItems = queuedItems.Count;

        if (totalItems == 0)
        {
            _logger.LogInformation("No queued people items to sync");
            config.LastPeopleSyncTime = DateTime.UtcNow;
            _configManager.SaveConfiguration();
            progress.Report(100);
            return;
        }

        _logger.LogInformation("Processing {Count} queued people items", totalItems);

        using var imageClient = syncImages ? _clientFactory.Create(config.SourceServerUrl, config.SourceServerApiKey) : null;

        var processedCount = 0;
        var successCount = 0;
        var errorCount = 0;

        foreach (var item in queuedItems)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var success = await SyncPersonItemAsync(item, database, syncImages, imageClient, cancellationToken).ConfigureAwait(false);

                if (success)
                {
                    successCount++;
                }
                else
                {
                    errorCount++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync person {PersonName}", item.PersonName);
                database.UpdatePeopleSyncItemStatusById(item.Id, BaseSyncStatus.Errored, ex.Message);
                errorCount++;
            }

            processedCount++;
            progress.Report((double)processedCount / totalItems * 100);
        }

        _logger.LogInformation(
            "People sync completed: {Success} synced, {Errors} errors out of {Total} items",
            successCount, errorCount, totalItems);

        config.LastPeopleSyncTime = DateTime.UtcNow;
        _configManager.SaveConfiguration();

        progress.Report(100);
    }

    private async Task<bool> SyncPersonItemAsync(
        PeopleSyncItem item,
        SyncDatabase database,
        bool syncImages,
        SourceServerClient? imageClient,
        CancellationToken cancellationToken)
    {
        // Find local person by name, then get full entity via GetItemById
        var personStub = _libraryManager.GetPerson(item.PersonName);
        if (personStub == null)
        {
            _logger.LogDebug("Local person not found for {PersonName}, skipping", item.PersonName);
            database.UpdatePeopleSyncItemStatusById(item.Id, BaseSyncStatus.Errored, "Local person not found");
            return false;
        }

        var localPerson = _libraryManager.GetItemById(personStub.Id);
        if (localPerson == null)
        {
            _logger.LogDebug("Could not load full person entity for {PersonName}, skipping", item.PersonName);
            database.UpdatePeopleSyncItemStatusById(item.Id, BaseSyncStatus.Errored, "Could not load person entity");
            return false;
        }

        var hasChanges = false;

        // Apply metadata from JSON blob
        if (item.HasMetadataChanges)
        {
            var metadataApplied = await ApplyMetadataAsync(localPerson, item, cancellationToken).ConfigureAwait(false);
            if (metadataApplied)
            {
                hasChanges = true;
            }
        }

        // Apply images
        if (syncImages && item.HasImagesChanges && imageClient != null && !string.IsNullOrEmpty(item.SourcePersonId))
        {
            var imageSuccess = await ApplyPersonImagesAsync(
                localPerson,
                Guid.Parse(item.SourcePersonId),
                imageClient,
                cancellationToken).ConfigureAwait(false);

            if (imageSuccess)
            {
                hasChanges = true;
            }
        }

        if (hasChanges)
        {
            await localPerson.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        database.UpdatePeopleSyncItemStatusById(item.Id, BaseSyncStatus.Synced);
        return true;
    }

    /// <summary>
    /// Applies metadata fields from SourceMetadataValue JSON blob to a local person entity.
    /// </summary>
    private async Task<bool> ApplyMetadataAsync(
        BaseItem localPerson,
        PeopleSyncItem item,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.SourceMetadataValue))
        {
            return false;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.SourceMetadataValue);
            if (metadata == null)
            {
                return false;
            }

            var hasChanges = false;

            // Name
            if (metadata.TryGetValue("Name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String)
            {
                var name = nameValue.GetString();
                if (!string.IsNullOrEmpty(name) && localPerson.Name != name)
                {
                    localPerson.Name = name;
                    hasChanges = true;
                }
            }

            // OriginalTitle
            if (metadata.TryGetValue("OriginalTitle", out var origTitleValue) && origTitleValue.ValueKind == JsonValueKind.String)
            {
                var origTitle = origTitleValue.GetString();
                if (localPerson.OriginalTitle != origTitle)
                {
                    localPerson.OriginalTitle = origTitle;
                    hasChanges = true;
                }
            }

            // SortName
            if (metadata.TryGetValue("SortName", out var sortNameValue) && sortNameValue.ValueKind == JsonValueKind.String)
            {
                var sortName = sortNameValue.GetString();
                if (localPerson.SortName != sortName)
                {
                    localPerson.SortName = sortName ?? string.Empty;
                    hasChanges = true;
                }
            }

            // ForcedSortName
            if (metadata.TryGetValue("ForcedSortName", out var forcedSortNameValue) && forcedSortNameValue.ValueKind == JsonValueKind.String)
            {
                var forcedSortName = forcedSortNameValue.GetString();
                if (localPerson.ForcedSortName != forcedSortName)
                {
                    localPerson.ForcedSortName = forcedSortName;
                    hasChanges = true;
                }
            }

            // Overview (biography)
            if (metadata.TryGetValue("Overview", out var overviewValue) && overviewValue.ValueKind == JsonValueKind.String)
            {
                var overview = overviewValue.GetString();
                if (localPerson.Overview != overview)
                {
                    localPerson.Overview = overview;
                    hasChanges = true;
                }
            }

            // PremiereDate (birth date)
            if (metadata.TryGetValue("PremiereDate", out var premiereDateValue))
            {
                DateTime? premiereDate = null;
                if (premiereDateValue.ValueKind == JsonValueKind.String)
                {
                    var dateStr = premiereDateValue.GetString();
                    if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                    {
                        premiereDate = parsed;
                    }
                }

                if (localPerson.PremiereDate != premiereDate)
                {
                    localPerson.PremiereDate = premiereDate;
                    hasChanges = true;
                }
            }

            // EndDate (death date)
            if (metadata.TryGetValue("EndDate", out var endDateValue))
            {
                DateTime? endDate = null;
                if (endDateValue.ValueKind == JsonValueKind.String)
                {
                    var dateStr = endDateValue.GetString();
                    if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                    {
                        endDate = parsed;
                    }
                }

                if (localPerson.EndDate != endDate)
                {
                    localPerson.EndDate = endDate;
                    hasChanges = true;
                }
            }

            // ProductionYear
            if (metadata.TryGetValue("ProductionYear", out var yearValue))
            {
                int? year = yearValue.ValueKind == JsonValueKind.Number
                    ? yearValue.GetInt32()
                    : null;
                if (localPerson.ProductionYear != year)
                {
                    localPerson.ProductionYear = year;
                    hasChanges = true;
                }
            }

            // Tags
            if (metadata.TryGetValue("Tags", out var tagsValue) && tagsValue.ValueKind == JsonValueKind.Array)
            {
                var tags = new List<string>();
                foreach (var tag in tagsValue.EnumerateArray())
                {
                    if (tag.ValueKind == JsonValueKind.String)
                    {
                        tags.Add(tag.GetString()!);
                    }
                }

                localPerson.Tags = tags.ToArray();
                hasChanges = true;
            }

            // ProviderIds
            if (metadata.TryGetValue("ProviderIds", out var providerIdsValue) && providerIdsValue.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in providerIdsValue.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var providerValue = prop.Value.GetString();
                        if (!string.IsNullOrEmpty(providerValue))
                        {
                            localPerson.SetProviderId(prop.Name, providerValue);
                            hasChanges = true;
                        }
                    }
                }
            }

            // LockedFields
            if (metadata.TryGetValue("LockedFields", out var lockedValue) && lockedValue.ValueKind == JsonValueKind.Array)
            {
                var lockedFieldsList = new List<MetadataField>();
                foreach (var f in lockedValue.EnumerateArray())
                {
                    if (f.ValueKind == JsonValueKind.String)
                    {
                        var fieldStr = f.GetString();
                        if (!string.IsNullOrEmpty(fieldStr) && Enum.TryParse<MetadataField>(fieldStr, out var field))
                        {
                            lockedFieldsList.Add(field);
                        }
                    }
                }

                localPerson.LockedFields = lockedFieldsList.ToArray();
                hasChanges = true;
            }

            // LockData (IsLocked)
            if (metadata.TryGetValue("LockData", out var lockDataValue))
            {
                bool? lockData = null;
                if (lockDataValue.ValueKind == JsonValueKind.True)
                {
                    lockData = true;
                }
                else if (lockDataValue.ValueKind == JsonValueKind.False)
                {
                    lockData = false;
                }

                if (lockData.HasValue && localPerson.IsLocked != lockData.Value)
                {
                    localPerson.IsLocked = lockData.Value;
                    hasChanges = true;
                }
            }

            if (hasChanges)
            {
                await localPerson.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }

            return hasChanges;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply metadata for person {PersonName}", item.PersonName);
            return false;
        }
    }

    private async Task<bool> ApplyPersonImagesAsync(
        BaseItem localPerson,
        Guid sourcePersonId,
        SourceServerClient imageClient,
        CancellationToken cancellationToken)
    {
        try
        {
            // Get image info from source for this person
            var sourceImages = await imageClient.GetItemImageInfoAsync(sourcePersonId, cancellationToken).ConfigureAwait(false);
            if (sourceImages == null || sourceImages.Count == 0)
            {
                return false;
            }

            var appliedAny = false;

            foreach (var imageInfo in sourceImages)
            {
                var imageType = imageInfo.ImageType?.ToString();
                if (string.IsNullOrEmpty(imageType))
                {
                    continue;
                }

                // Download the image
                var (stream, contentType) = await imageClient.DownloadItemImageAsync(
                    sourcePersonId,
                    imageType,
                    imageInfo.ImageIndex,
                    cancellationToken).ConfigureAwait(false);

                if (stream == null)
                {
                    continue;
                }

                // Save to temp file then apply
                var tempPath = Path.GetTempFileName();
                try
                {
                    using (var fileStream = File.Create(tempPath))
                    {
                        await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                    }

                    await using var _ = stream.ConfigureAwait(false);

                    // Parse the image type enum
                    if (Enum.TryParse<ImageType>(imageType, true, out var parsedImageType))
                    {
                        using var fileStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        await _providerManager.SaveImage(
                            localPerson,
                            fileStream,
                            contentType ?? "image/jpeg",
                            parsedImageType,
                            imageInfo.ImageIndex,
                            cancellationToken).ConfigureAwait(false);

                        appliedAny = true;
                        _logger.LogDebug("Applied {ImageType} image for person {PersonName}", imageType, localPerson.Name);
                    }
                }
                finally
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch
                    {
                        // Best effort cleanup
                    }
                }
            }

            return appliedAny;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply images for person {PersonName}", localPerson.Name);
            return false;
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(14).Ticks
            }
        };
    }
}
