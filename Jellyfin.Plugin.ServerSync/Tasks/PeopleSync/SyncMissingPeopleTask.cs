using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    public string Description => "Applies queued people sync changes (overview, provider IDs, images) to local person entities.";

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
        // Find local person by name
        var localPerson = _libraryManager.GetPerson(item.PersonName);
        if (localPerson == null)
        {
            _logger.LogDebug("Local person not found for {PersonName}, skipping", item.PersonName);
            database.UpdatePeopleSyncItemStatusById(item.Id, BaseSyncStatus.Errored, "Local person not found");
            return false;
        }

        var hasChanges = false;

        // Apply overview
        if (item.HasOverviewChanges)
        {
            localPerson.Overview = item.SourceOverview;
            hasChanges = true;
            _logger.LogDebug("Updated overview for person {PersonName}", item.PersonName);
        }

        // Apply provider IDs
        if (item.HasProviderIdChanges && !string.IsNullOrEmpty(item.SourceProviderIds))
        {
            try
            {
                var providerIds = JsonSerializer.Deserialize<Dictionary<string, string>>(item.SourceProviderIds);
                if (providerIds != null)
                {
                    foreach (var kvp in providerIds)
                    {
                        localPerson.SetProviderId(kvp.Key, kvp.Value);
                    }

                    hasChanges = true;
                    _logger.LogDebug("Updated provider IDs for person {PersonName}", item.PersonName);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse provider IDs for person {PersonName}", item.PersonName);
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
