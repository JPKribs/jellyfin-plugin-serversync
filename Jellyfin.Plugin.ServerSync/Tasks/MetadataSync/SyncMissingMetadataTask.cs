using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Scheduled task to sync metadata from the source server.
/// Applies queued changes from the metadata sync table.
/// </summary>
public class SyncMissingMetadataTask : IScheduledTask
{
    private readonly ILogger<SyncMissingMetadataTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISyncDatabaseProvider _databaseProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncMissingMetadataTask"/> class.
    /// </summary>
    public SyncMissingMetadataTask(
        ILogger<SyncMissingMetadataTask> logger,
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
    public string Name => "Sync Metadata";

    /// <inheritdoc />
    public string Key => "ServerSyncMissingMetadata";

    /// <inheritdoc />
    public string Description => "Applies queued metadata changes from the sync table to the local server.";

    /// <inheritdoc />
    public string Category => "Metadata Sync";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = _configManager.Configuration;

        // Check if metadata sync is enabled
        if (!config.EnableMetadataSync)
        {
            return;
        }

        // Validate source server configuration
        if (string.IsNullOrWhiteSpace(config.SourceServerUrl) ||
            string.IsNullOrWhiteSpace(config.SourceServerApiKey))
        {
            _logger.LogWarning("Metadata sync skipped: source server not configured");
            return;
        }

        // Get enabled library mappings
        var enabledLibraryMappings = config.LibraryMappings?.Where(m => m.IsEnabled).ToList() ?? new List<LibraryMapping>();

        if (enabledLibraryMappings.Count == 0)
        {
            _logger.LogDebug("Metadata sync skipped: no enabled library mappings");
            return;
        }

        // Get enabled category flags
        var syncMetadata = config.MetadataSyncMetadata;
        var syncGenres = config.MetadataSyncGenres;
        var syncTags = config.MetadataSyncTags;
        var syncImages = config.MetadataSyncImages;
        var syncPeople = config.MetadataSyncPeople;
        var syncStudios = config.MetadataSyncStudios;

        if (!syncMetadata && !syncImages && !syncPeople && !syncStudios)
        {
            _logger.LogDebug("Metadata sync skipped: no categories enabled");
            return;
        }

        _logger.LogInformation("Starting metadata sync");

        var database = _databaseProvider.Database;

        // Apply queued changes
        _logger.LogInformation("Applying queued metadata changes");

        // Get all queued metadata items, ordered so parents sync before children
        var queuedItems = database.GetMetadataSyncItemsByStatus(BaseSyncStatus.Queued);

        // Sort: folder items first (by hierarchy depth), then leaf items
        queuedItems.Sort((a, b) =>
        {
            var orderA = GetItemTypeOrder(a.ItemType);
            var orderB = GetItemTypeOrder(b.ItemType);
            if (orderA != orderB)
            {
                return orderA.CompareTo(orderB);
            }

            return string.Compare(a.ItemName, b.ItemName, StringComparison.OrdinalIgnoreCase);
        });

        var totalItems = queuedItems.Count;

        if (totalItems == 0)
        {
            _logger.LogInformation("No queued metadata items to sync");

            // Update last sync time
            config.LastMetadataSyncTime = DateTime.UtcNow;
            _configManager.SaveConfiguration();

            progress.Report(100);
            return;
        }

        _logger.LogInformation("Processing {Count} queued metadata items", totalItems);

        // Create a shared SourceServerClient for image downloads (reused across all items)
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
                var success = await SyncMetadataItemAsync(item, database, syncMetadata, syncGenres, syncTags, syncImages, syncPeople, syncStudios, imageClient, cancellationToken).ConfigureAwait(false);

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
                _logger.LogError(ex, "Failed to sync metadata item {ItemName}", item.ItemName);
                item.Status = BaseSyncStatus.Errored;
                item.ErrorMessage = ex.Message;
                item.StatusDate = DateTime.UtcNow;
                database.UpsertMetadataSyncItem(item);
                errorCount++;
            }

            processedCount++;
            progress.Report((double)processedCount / totalItems * 100);
        }

        // Update last sync time
        config.LastMetadataSyncTime = DateTime.UtcNow;
        _configManager.SaveConfiguration();

        _logger.LogInformation(
            "Metadata sync completed: {Success} succeeded, {Error} failed out of {Total}",
            successCount, errorCount, totalItems);

        progress.Report(100);
    }

    /// <summary>
    /// Syncs a single metadata item to the local server (all enabled categories).
    /// </summary>
    private async Task<bool> SyncMetadataItemAsync(
        MetadataSyncItem item,
        SyncDatabase database,
        bool syncMetadata,
        bool syncGenres,
        bool syncTags,
        bool syncImages,
        bool syncPeople,
        bool syncStudios,
        SourceServerClient? sourceClient,
        CancellationToken cancellationToken)
    {
        // Validate we have a local item ID
        if (string.IsNullOrEmpty(item.LocalItemId))
        {
            _logger.LogWarning("Cannot sync metadata for {ItemName}: local item not found", item.ItemName);
            item.Status = BaseSyncStatus.Errored;
            item.ErrorMessage = "Local item not found";
            item.StatusDate = DateTime.UtcNow;
            database.UpsertMetadataSyncItem(item);
            return false;
        }

        // Parse local item ID
        if (!Guid.TryParse(item.LocalItemId, out var localItemId))
        {
            _logger.LogWarning("Cannot sync metadata for {ItemName}: invalid item ID", item.ItemName);
            item.Status = BaseSyncStatus.Errored;
            item.ErrorMessage = "Invalid item ID";
            item.StatusDate = DateTime.UtcNow;
            database.UpsertMetadataSyncItem(item);
            return false;
        }

        // Get the local item
        var localItem = _libraryManager.GetItemById(localItemId);
        if (localItem == null)
        {
            _logger.LogWarning("Cannot sync metadata for {ItemName}: local item not found in library", item.ItemName);
            item.Status = BaseSyncStatus.Errored;
            item.ErrorMessage = "Local item not found in library";
            item.StatusDate = DateTime.UtcNow;
            database.UpsertMetadataSyncItem(item);
            return false;
        }

        var allSucceeded = true;
        var syncedCategories = new List<string>();

        // Sync metadata if enabled and has changes
        if (syncMetadata && item.HasMetadataChanges)
        {
            var success = await ApplyMetadataAsync(localItem, item, syncGenres, syncTags, cancellationToken).ConfigureAwait(false);
            if (success)
            {
                // Update local value to match source after sync
                item.LocalMetadataValue = item.SourceMetadataValue;
                syncedCategories.Add("Metadata");
            }
            else
            {
                allSucceeded = false;
                _logger.LogWarning("Failed to sync metadata for {ItemName}", item.ItemName);
            }
        }

        // Sync images if enabled and has changes
        if (syncImages && item.HasImagesChanges)
        {
            var success = await ApplyImagesAsync(localItem, item, sourceClient, cancellationToken).ConfigureAwait(false);
            if (success)
            {
                // Track what source hash we synced - used for comparison on refresh
                item.SyncedImagesHash = item.SourceImagesHash;
                syncedCategories.Add("Images");
            }
            else
            {
                allSucceeded = false;
                _logger.LogWarning("Failed to sync images for {ItemName}", item.ItemName);
            }
        }

        // Sync people if enabled and has changes
        if (syncPeople && item.HasPeopleChanges)
        {
            var success = await ApplyPeopleAsync(localItem, item, cancellationToken).ConfigureAwait(false);
            if (success)
            {
                // Update local value to match source after sync
                item.LocalPeopleValue = item.SourcePeopleValue;
                syncedCategories.Add("People");
            }
            else
            {
                allSucceeded = false;
                _logger.LogWarning("Failed to sync people for {ItemName}", item.ItemName);
            }
        }

        // Sync studios if enabled and has changes
        if (syncStudios && item.HasStudiosChanges)
        {
            var success = await ApplyStudiosAsync(localItem, item, cancellationToken).ConfigureAwait(false);
            if (success)
            {
                // Update local value to match source after sync
                item.LocalStudiosValue = item.SourceStudiosValue;
                syncedCategories.Add("Studios");
            }
            else
            {
                allSucceeded = false;
                _logger.LogWarning("Failed to sync studios for {ItemName}", item.ItemName);
            }
        }

        if (allSucceeded && syncedCategories.Count > 0)
        {
            _logger.LogDebug("Synced {Categories} for {ItemName}", string.Join(", ", syncedCategories), item.ItemName);

            // Update item status
            item.Status = BaseSyncStatus.Synced;
            item.LastSyncTime = DateTime.UtcNow;
            item.StatusDate = DateTime.UtcNow;
            item.ErrorMessage = null;

            database.UpsertMetadataSyncItem(item);
            return true;
        }
        else if (!allSucceeded)
        {
            item.Status = BaseSyncStatus.Errored;
            item.ErrorMessage = "Failed to apply some metadata categories";
            item.StatusDate = DateTime.UtcNow;
            database.UpsertMetadataSyncItem(item);
            return false;
        }
        else
        {
            // No changes to sync - mark as synced
            item.Status = BaseSyncStatus.Synced;
            item.LastSyncTime = DateTime.UtcNow;
            item.StatusDate = DateTime.UtcNow;
            item.ErrorMessage = null;
            database.UpsertMetadataSyncItem(item);
            return true;
        }
    }

    /// <summary>
    /// Applies metadata fields to a local item.
    /// </summary>
    private async Task<bool> ApplyMetadataAsync(
        MediaBrowser.Controller.Entities.BaseItem localItem,
        MetadataSyncItem item,
        bool syncGenres,
        bool syncTags,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.SourceMetadataValue))
        {
            return true; // Nothing to apply
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.SourceMetadataValue);
            if (metadata == null)
            {
                return false;
            }

            var hasChanges = false;

            // Apply each metadata field
            if (metadata.TryGetValue("Name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String)
            {
                var name = nameValue.GetString();
                if (!string.IsNullOrEmpty(name) && localItem.Name != name)
                {
                    localItem.Name = name;
                    hasChanges = true;
                }
            }

            if (metadata.TryGetValue("OriginalTitle", out var origTitleValue) && origTitleValue.ValueKind == JsonValueKind.String)
            {
                var origTitle = origTitleValue.GetString();
                if (localItem.OriginalTitle != origTitle)
                {
                    localItem.OriginalTitle = origTitle;
                    hasChanges = true;
                }
            }

            if (metadata.TryGetValue("Overview", out var overviewValue) && overviewValue.ValueKind == JsonValueKind.String)
            {
                var overview = overviewValue.GetString();
                if (localItem.Overview != overview)
                {
                    localItem.Overview = overview;
                    hasChanges = true;
                }
            }

            if (metadata.TryGetValue("OfficialRating", out var ratingValue) && ratingValue.ValueKind == JsonValueKind.String)
            {
                var rating = ratingValue.GetString();
                if (localItem.OfficialRating != rating)
                {
                    localItem.OfficialRating = rating;
                    hasChanges = true;
                }
            }

            if (metadata.TryGetValue("CommunityRating", out var commRatingValue))
            {
                float? commRating = commRatingValue.ValueKind == JsonValueKind.Number
                    ? commRatingValue.GetSingle()
                    : null;
                if (localItem.CommunityRating != commRating)
                {
                    localItem.CommunityRating = commRating;
                    hasChanges = true;
                }
            }

            if (metadata.TryGetValue("CriticRating", out var criticRatingValue))
            {
                float? criticRating = criticRatingValue.ValueKind == JsonValueKind.Number
                    ? criticRatingValue.GetSingle()
                    : null;
                if (localItem.CriticRating != criticRating)
                {
                    localItem.CriticRating = criticRating;
                    hasChanges = true;
                }
            }

            if (metadata.TryGetValue("ProductionYear", out var yearValue))
            {
                int? year = yearValue.ValueKind == JsonValueKind.Number
                    ? yearValue.GetInt32()
                    : null;
                if (localItem.ProductionYear != year)
                {
                    localItem.ProductionYear = year;
                    hasChanges = true;
                }
            }

            // Only sync Genres if enabled in config
            if (syncGenres && metadata.TryGetValue("Genres", out var genresValue) && genresValue.ValueKind == JsonValueKind.Array)
            {
                var genres = new List<string>();
                foreach (var genre in genresValue.EnumerateArray())
                {
                    if (genre.ValueKind == JsonValueKind.String)
                    {
                        genres.Add(genre.GetString()!);
                    }
                }

                localItem.Genres = genres.ToArray();
                hasChanges = true;
            }

            // Only sync Tags if enabled in config
            if (syncTags && metadata.TryGetValue("Tags", out var tagsValue) && tagsValue.ValueKind == JsonValueKind.Array)
            {
                var tags = new List<string>();
                foreach (var tag in tagsValue.EnumerateArray())
                {
                    if (tag.ValueKind == JsonValueKind.String)
                    {
                        tags.Add(tag.GetString()!);
                    }
                }

                localItem.Tags = tags.ToArray();
                hasChanges = true;
            }

            // Tagline (singular string on BaseItem)
            if (metadata.TryGetValue("Tagline", out var taglineValue) && taglineValue.ValueKind == JsonValueKind.String)
            {
                var tagline = taglineValue.GetString();
                if (localItem.Tagline != tagline)
                {
                    localItem.Tagline = tagline;
                    hasChanges = true;
                }
            }

            // SortName
            if (metadata.TryGetValue("SortName", out var sortNameValue) && sortNameValue.ValueKind == JsonValueKind.String)
            {
                var sortName = sortNameValue.GetString();
                if (localItem.SortName != sortName)
                {
                    localItem.SortName = sortName ?? string.Empty;
                    hasChanges = true;
                }
            }

            // ForcedSortName
            if (metadata.TryGetValue("ForcedSortName", out var forcedSortNameValue) && forcedSortNameValue.ValueKind == JsonValueKind.String)
            {
                var forcedSortName = forcedSortNameValue.GetString();
                if (localItem.ForcedSortName != forcedSortName)
                {
                    localItem.ForcedSortName = forcedSortName;
                    hasChanges = true;
                }
            }

            // CustomRating
            if (metadata.TryGetValue("CustomRating", out var customRatingValue) && customRatingValue.ValueKind == JsonValueKind.String)
            {
                var customRating = customRatingValue.GetString();
                if (localItem.CustomRating != customRating)
                {
                    localItem.CustomRating = customRating;
                    hasChanges = true;
                }
            }

            // PremiereDate
            if (metadata.TryGetValue("PremiereDate", out var premiereDateValue))
            {
                DateTime? premiereDate = null;
                if (premiereDateValue.ValueKind == JsonValueKind.String)
                {
                    var dateStr = premiereDateValue.GetString();
                    if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                    {
                        premiereDate = parsed;
                    }
                }

                if (localItem.PremiereDate != premiereDate)
                {
                    localItem.PremiereDate = premiereDate;
                    hasChanges = true;
                }
            }

            // EndDate
            if (metadata.TryGetValue("EndDate", out var endDateValue))
            {
                DateTime? endDate = null;
                if (endDateValue.ValueKind == JsonValueKind.String)
                {
                    var dateStr = endDateValue.GetString();
                    if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                    {
                        endDate = parsed;
                    }
                }

                if (localItem.EndDate != endDate)
                {
                    localItem.EndDate = endDate;
                    hasChanges = true;
                }
            }

            // IndexNumber (episode number)
            if (metadata.TryGetValue("IndexNumber", out var indexNumValue))
            {
                int? indexNum = indexNumValue.ValueKind == JsonValueKind.Number
                    ? indexNumValue.GetInt32()
                    : null;
                if (localItem.IndexNumber != indexNum)
                {
                    localItem.IndexNumber = indexNum;
                    hasChanges = true;
                }
            }

            // ParentIndexNumber (season number)
            if (metadata.TryGetValue("ParentIndexNumber", out var parentIndexNumValue))
            {
                int? parentIndexNum = parentIndexNumValue.ValueKind == JsonValueKind.Number
                    ? parentIndexNumValue.GetInt32()
                    : null;
                if (localItem.ParentIndexNumber != parentIndexNum)
                {
                    localItem.ParentIndexNumber = parentIndexNum;
                    hasChanges = true;
                }
            }

            // PreferredMetadataCountryCode
            if (metadata.TryGetValue("PreferredMetadataCountryCode", out var countryCodeValue) && countryCodeValue.ValueKind == JsonValueKind.String)
            {
                var countryCode = countryCodeValue.GetString();
                if (localItem.PreferredMetadataCountryCode != countryCode)
                {
                    localItem.PreferredMetadataCountryCode = countryCode;
                    hasChanges = true;
                }
            }

            // PreferredMetadataLanguage
            if (metadata.TryGetValue("PreferredMetadataLanguage", out var langValue) && langValue.ValueKind == JsonValueKind.String)
            {
                var lang = langValue.GetString();
                if (localItem.PreferredMetadataLanguage != lang)
                {
                    localItem.PreferredMetadataLanguage = lang;
                    hasChanges = true;
                }
            }

            if (metadata.TryGetValue("ProviderIds", out var providerIdsValue) && providerIdsValue.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in providerIdsValue.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var providerValue = prop.Value.GetString();
                        if (!string.IsNullOrEmpty(providerValue))
                        {
                            localItem.SetProviderId(prop.Name, providerValue);
                            hasChanges = true;
                        }
                    }
                }
            }

            // Video-specific properties - only apply if item is a Video
            var localVideo = localItem as MediaBrowser.Controller.Entities.Video;

            // Note: Taglines is not directly settable on local BaseItem/Video - will be synced as display-only

            // AspectRatio (Video-specific)
            if (localVideo != null && metadata.TryGetValue("AspectRatio", out var aspectValue))
            {
                var aspectRatio = aspectValue.ValueKind == JsonValueKind.String ? aspectValue.GetString() : null;
                if (localVideo.AspectRatio != aspectRatio)
                {
                    localVideo.AspectRatio = aspectRatio;
                    hasChanges = true;
                }
            }

            // Video3DFormat (Video-specific)
            if (localVideo != null && metadata.TryGetValue("Video3DFormat", out var video3DValue))
            {
                MediaBrowser.Model.Entities.Video3DFormat? format = null;
                if (video3DValue.ValueKind == JsonValueKind.String)
                {
                    var formatStr = video3DValue.GetString();
                    if (!string.IsNullOrEmpty(formatStr) && Enum.TryParse<MediaBrowser.Model.Entities.Video3DFormat>(formatStr, out var parsed))
                    {
                        format = parsed;
                    }
                }

                if (localVideo.Video3DFormat != format)
                {
                    localVideo.Video3DFormat = format;
                    hasChanges = true;
                }
            }

            // LockedFields (BaseItem property)
            if (metadata.TryGetValue("LockedFields", out var lockedValue) && lockedValue.ValueKind == JsonValueKind.Array)
            {
                var lockedFieldsList = new List<MediaBrowser.Model.Entities.MetadataField>();
                foreach (var f in lockedValue.EnumerateArray())
                {
                    if (f.ValueKind == JsonValueKind.String)
                    {
                        var fieldStr = f.GetString();
                        if (!string.IsNullOrEmpty(fieldStr) && Enum.TryParse<MediaBrowser.Model.Entities.MetadataField>(fieldStr, out var field))
                        {
                            lockedFieldsList.Add(field);
                        }
                    }
                }

                localItem.LockedFields = lockedFieldsList.ToArray();
                hasChanges = true;
            }

            // LockData (IsLocked - lock item to prevent future metadata changes)
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

                if (lockData.HasValue && localItem.IsLocked != lockData.Value)
                {
                    localItem.IsLocked = lockData.Value;
                    hasChanges = true;
                }
            }

            if (hasChanges)
            {
                await localItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply metadata for {ItemName}", item.ItemName);
            return false;
        }
    }

    /// <summary>
    /// Applies images from the source server to a local item.
    /// Deletes existing local images and replaces them with images from the source.
    /// </summary>
    private async Task<bool> ApplyImagesAsync(
        MediaBrowser.Controller.Entities.BaseItem localItem,
        MetadataSyncItem item,
        SourceServerClient? sourceClient,
        CancellationToken cancellationToken)
    {
        if (sourceClient == null)
        {
            _logger.LogWarning("No source server client available for image sync");
            return false;
        }

        try
        {
            // Parse source image info - new format is Dictionary<imageType, List<ImageInfoDto>>
            if (string.IsNullOrEmpty(item.SourceImagesValue))
            {
                return true; // No images on source
            }

            var sourceImagesByType = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(item.SourceImagesValue);
            if (sourceImagesByType == null || sourceImagesByType.Count == 0)
            {
                return true;
            }

            if (!Guid.TryParse(item.SourceItemId, out var sourceItemGuid))
            {
                _logger.LogWarning("Invalid source item ID for image sync: {SourceItemId}", item.SourceItemId);
                return false;
            }

            // Process each image type from source.
            // Two-pass approach: first download all images to temp files to verify success,
            // then apply them. This prevents data loss (deleting existing images before
            // confirming all downloads succeed) while avoiding holding all image data in memory.
            foreach (var kvp in sourceImagesByType)
            {
                var imageTypeName = kvp.Key;
                var sourceImages = kvp.Value;

                if (!Enum.TryParse<ImageType>(imageTypeName, out var imageType))
                {
                    _logger.LogWarning("Unknown image type: {ImageType}", imageTypeName);
                    continue;
                }

                // Pass 1: Download all images for this type to temp files
                var tempFiles = new List<(int Index, string TempPath, string ContentType)>();
                try
                {
                    var allDownloadsSucceeded = true;

                    for (int i = 0; i < sourceImages.Count; i++)
                    {
                        int? imageIndex = (imageType == ImageType.Backdrop || sourceImages.Count > 1) ? i : null;

                        try
                        {
                            var (imageStream, contentType) = await sourceClient.DownloadItemImageAsync(
                                sourceItemGuid, imageTypeName, imageIndex, cancellationToken).ConfigureAwait(false);

                            if (imageStream == null)
                            {
                                _logger.LogWarning("Failed to download image {ImageType}/{Index} for {ItemName}",
                                    imageTypeName, i, localItem.Name);
                                allDownloadsSucceeded = false;
                                continue;
                            }

                            var tempPath = Path.GetTempFileName();
                            try
                            {
                                using (imageStream)
                                {
                                    using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920);
                                    await imageStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                                }

                                tempFiles.Add((i, tempPath, contentType ?? "image/jpeg"));
                            }
                            catch
                            {
                                try { File.Delete(tempPath); } catch (IOException) { }
                                throw;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error downloading {ImageType}/{Index} image for {ItemName}",
                                imageTypeName, i, localItem.Name);
                            allDownloadsSucceeded = false;
                        }
                    }

                    // Only replace existing images if ALL downloads succeeded for this type.
                    // Partial success would delete existing images and replace with fewer — data loss.
                    if (!allDownloadsSucceeded)
                    {
                        _logger.LogWarning(
                            "Not all {ImageType} downloads succeeded for {ItemName} ({Downloaded}/{Total}), keeping existing images",
                            imageTypeName, localItem.Name, tempFiles.Count, sourceImages.Count);
                        continue;
                    }

                    if (tempFiles.Count == 0)
                    {
                        // No source images and all "downloads" succeeded (vacuously) — nothing to do
                        continue;
                    }

                    // Pass 2: All downloads succeeded — safe to delete existing images and apply
                    var existingImages = localItem.GetImages(imageType).ToList();
                    foreach (var existingImage in existingImages)
                    {
                        try
                        {
                            localItem.RemoveImage(existingImage);
                            _logger.LogDebug("Removed existing {ImageType} image for {ItemName}", imageTypeName, localItem.Name);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to remove existing {ImageType} image for {ItemName}", imageTypeName, localItem.Name);
                        }
                    }

                    // Stream each temp file into the provider manager one at a time
                    foreach (var (index, tempPath, contentType) in tempFiles)
                    {
                        try
                        {
                            using var fileStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                            await _providerManager.SaveImage(
                                localItem,
                                fileStream,
                                contentType,
                                imageType,
                                index,
                                cancellationToken).ConfigureAwait(false);
                            _logger.LogDebug("Saved {ImageType} image for {ItemName}", imageTypeName, localItem.Name);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error saving {ImageType}/{Index} image for {ItemName}",
                                imageTypeName, index, localItem.Name);
                        }
                    }
                }
                finally
                {
                    // Clean up all temp files even on exception
                    foreach (var (_, tempPath, _) in tempFiles)
                    {
                        try { File.Delete(tempPath); } catch (IOException) { }
                    }
                }
            }

            // Save the item to persist image changes
            await localItem.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply images for {ItemName}", item.ItemName);
            return false;
        }
    }

    /// <summary>
    /// Applies people to a local item.
    /// </summary>
    private async Task<bool> ApplyPeopleAsync(
        MediaBrowser.Controller.Entities.BaseItem localItem,
        MetadataSyncItem item,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.SourcePeopleValue))
        {
            return true; // Nothing to apply
        }

        try
        {
            var peopleList = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(item.SourcePeopleValue);
            if (peopleList == null)
            {
                return false;
            }

            var people = new List<MediaBrowser.Controller.Entities.PersonInfo>();
            foreach (var person in peopleList)
            {
                var personInfo = new MediaBrowser.Controller.Entities.PersonInfo();

                if (person.TryGetValue("Name", out var name))
                {
                    personInfo.Name = name;
                }

                if (person.TryGetValue("Role", out var role))
                {
                    personInfo.Role = role;
                }

                if (person.TryGetValue("Type", out var type) && Enum.TryParse<Jellyfin.Data.Enums.PersonKind>(type, out var personKind))
                {
                    personInfo.Type = personKind;
                }

                if (!string.IsNullOrEmpty(personInfo.Name))
                {
                    people.Add(personInfo);
                }
            }

            _libraryManager.UpdatePeople(localItem, people);
            await localItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply people for {ItemName}", item.ItemName);
            return false;
        }
    }

    /// <summary>
    /// Applies studios to a local item.
    /// </summary>
    private async Task<bool> ApplyStudiosAsync(
        MediaBrowser.Controller.Entities.BaseItem localItem,
        MetadataSyncItem item,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.SourceStudiosValue))
        {
            return true; // Nothing to apply
        }

        try
        {
            var studiosList = JsonSerializer.Deserialize<List<string>>(item.SourceStudiosValue);
            if (studiosList == null)
            {
                return false;
            }

            // Apply studios directly - Jellyfin will create/find the studio entities by name
            localItem.Studios = studiosList.ToArray();
            await localItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply studios for {ItemName}", item.ItemName);
            return false;
        }
    }

    /// <summary>
    /// Returns a sort order for item types to ensure parents sync before children.
    /// Lower values sync first.
    /// </summary>
    private static int GetItemTypeOrder(string? itemType)
    {
        return itemType switch
        {
            "Series" or "MusicArtist" => 0,
            "Season" or "MusicAlbum" or "BoxSet" => 1,
            _ => 2 // Episodes, Movies, Audio, Video, and unknown types
        };
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(12).Ticks
            }
        };
    }
}
