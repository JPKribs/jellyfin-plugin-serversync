using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using TaskTriggerInfo = MediaBrowser.Model.Tasks.TaskTriggerInfo;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Apply phase for Metadata sync. Reads queued
/// <see cref="MetadataSyncItem"/> rows in parent-first order and applies
/// each enabled category (Metadata, Images, People, Studios) to the local
/// item via the four <c>ApplyXxxAsync</c> private helpers. A category that
/// succeeds is per-category <c>MarkSynced</c>'d so its hash short-circuit
/// is honored on the next Refresh; a category that fails causes
/// <see cref="ApplyAsync"/> to throw, which the base class translates into
/// <see cref="SyncStatus.Errored"/>.
/// </summary>
public class SyncMissingMetadataTask : SyncQueueTaskBase<MetadataSyncItem, (string SourceLibraryId, string SourceItemId)>
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public SyncMissingMetadataTask(
        ILogger<SyncMissingMetadataTask> logger,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        ISourceServerClientFactory clientFactory,
        IPluginConfigurationManager configManager,
        MetadataSyncTableManager manager)
        : base(logger, manager, clientFactory, configManager)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
    }

    /// <inheritdoc />
    public override string Name => "Sync Metadata";

    /// <inheritdoc />
    public override string Key => "ServerSyncMissingMetadata";

    /// <inheritdoc />
    public override string Description => "Applies queued metadata changes from the sync table to the local server.";

    /// <inheritdoc />
    public override string Category => "Metadata Sync";

    /// <inheritdoc />
    protected override string ModuleMutexKey => "Metadata";

    /// <inheritdoc />
    protected override bool IsEnabled()
    {
        var config = ConfigManager.Configuration;
        if (!config.EnableMetadataSync) return false;
        if (string.IsNullOrWhiteSpace(config.SourceServerUrl) || string.IsNullOrWhiteSpace(config.SourceServerApiKey)) return false;
        if (config.GetEnabledLibraryMappings().Count == 0) return false;
        return config.MetadataSyncMetadata || config.MetadataSyncImages
            || config.MetadataSyncPeople || config.MetadataSyncStudios;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sort: folder-type items first (Series → Season → MusicArtist →
    /// MusicAlbum → BoxSet) then leaf items, so parent metadata is in
    /// place before children would inherit anything from it.
    /// </remarks>
    protected override IList<MetadataSyncItem> GetItemsToApply()
    {
        var items = Manager.GetByStatus(SyncStatus.Queued).ToList();
        items.Sort((a, b) =>
        {
            var orderA = GetItemTypeOrder(a.ItemType);
            var orderB = GetItemTypeOrder(b.ItemType);
            if (orderA != orderB)
            {
                return orderA.CompareTo(orderB);
            }

            return string.Compare(a.ItemName, b.ItemName, StringComparison.OrdinalIgnoreCase);
        });

        return items;
    }

    /// <inheritdoc />
    protected override async Task ApplyAsync(MetadataSyncItem record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrEmpty(record.LocalItemId))
        {
            throw new InvalidOperationException("Local item not found");
        }

        if (!Guid.TryParse(record.LocalItemId, out var localItemId))
        {
            throw new InvalidOperationException("Invalid item ID");
        }

        var localItem = _libraryManager.GetItemById(localItemId);
        if (localItem == null)
        {
            throw new InvalidOperationException("Local item not found in library");
        }

        var config = ConfigManager.Configuration;
        var failures = new List<string>();
        var synced = new List<string>();

        if (config.MetadataSyncMetadata && record.HasMetadataChanges)
        {
            if (await ApplyMetadataAsync(localItem, record, config.MetadataSyncGenres, config.MetadataSyncTags, cancellationToken).ConfigureAwait(false))
            {
                record.Metadata.MarkSynced();
                synced.Add("Metadata");
            }
            else
            {
                failures.Add("Metadata");
            }
        }

        if (config.MetadataSyncImages && record.HasImagesChanges)
        {
            if (await ApplyImagesAsync(localItem, record, Client, cancellationToken).ConfigureAwait(false))
            {
                record.Images.MarkSynced();
                synced.Add("Images");
            }
            else
            {
                failures.Add("Images");
            }
        }

        if (config.MetadataSyncPeople && record.HasPeopleChanges)
        {
            if (await ApplyPeopleAsync(localItem, record, cancellationToken).ConfigureAwait(false))
            {
                record.People.MarkSynced();
                synced.Add("People");
            }
            else
            {
                failures.Add("People");
            }
        }

        if (config.MetadataSyncStudios && record.HasStudiosChanges)
        {
            if (await ApplyStudiosAsync(localItem, record, cancellationToken).ConfigureAwait(false))
            {
                record.Studios.MarkSynced();
                synced.Add("Studios");
            }
            else
            {
                failures.Add("Studios");
            }
        }

        if (failures.Count > 0)
        {
            // Categories that succeeded keep their per-category MarkSynced
            // state on the record — the base class will Upsert the record
            // as Errored, persisting the partial progress so the next run
            // doesn't redo the categories that already succeeded.
            throw new InvalidOperationException($"Failed to apply: {string.Join(", ", failures)}");
        }

        if (synced.Count > 0)
        {
            Logger.LogDebug("Synced {Categories} for {ItemName}", string.Join(", ", synced), record.ItemName);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// No-op — per-category <see cref="SyncableValue{T}.MarkSynced"/> calls
    /// already happened inside <see cref="ApplyAsync"/>. Calling
    /// <see cref="MetadataSyncItem.MarkSynced"/> here would re-mark
    /// categories that <see cref="ApplyAsync"/> intentionally skipped (e.g.
    /// because the user disabled them in config), which would break the
    /// short-circuit on the next Refresh once they're re-enabled.
    /// </remarks>
    protected override void OnApplySucceeded(MetadataSyncItem record)
    {
        // Intentionally empty.
    }

    /// <inheritdoc />
    protected override Task FinalizeAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ConfigManager.Configuration.LastMetadataSyncTime = DateTime.UtcNow;
        try
        {
            ConfigManager.SaveConfiguration();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to save metadata sync end time");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(12).Ticks
        }
    };

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
        if (string.IsNullOrEmpty(item.Metadata.Source))
        {
            return true; // Nothing to apply
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.Metadata.Source);
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

            // PremiereDate (date-only semantic — compare by calendar date to avoid
            // timezone-driven false diffs when servers are in different TZs)
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

                if (!JsonComparisonUtility.DateOnlyEquals(localItem.PremiereDate, premiereDate))
                {
                    localItem.PremiereDate = premiereDate;
                    hasChanges = true;
                }
            }

            // EndDate (date-only semantic)
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

                if (!JsonComparisonUtility.DateOnlyEquals(localItem.EndDate, endDate))
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
                // Reconcile providers so local matches source exactly. Without
                // the cleanup pass, local-only entries (e.g. a Tmdb id that
                // was scraped locally but isn't on source) persist forever
                // and report as "still desynced" on every refresh.
                var sourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in providerIdsValue.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrEmpty(prop.Value.GetString()))
                    {
                        sourceKeys.Add(prop.Name);
                    }
                }

                if (localItem.ProviderIds != null)
                {
                    var toRemove = localItem.ProviderIds.Keys
                        .Where(k => !sourceKeys.Contains(k))
                        .ToList();
                    foreach (var key in toRemove)
                    {
                        localItem.ProviderIds.Remove(key);
                        hasChanges = true;
                    }
                }

                foreach (var prop in providerIdsValue.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var providerValue = prop.Value.GetString();
                        if (!string.IsNullOrEmpty(providerValue)
                            && localItem.GetProviderId(prop.Name) != providerValue)
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
            Logger.LogError(ex, "Failed to apply metadata for {ItemName}", item.ItemName);
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
            Logger.LogWarning("No source server client available for image sync");
            return false;
        }

        try
        {
            // Parse source image info - new format is Dictionary<imageType, List<ImageInfoDto>>
            if (string.IsNullOrEmpty(item.Images.Source))
            {
                return true; // No images on source
            }

            var sourceImagesByType = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(item.Images.Source);
            if (sourceImagesByType == null || sourceImagesByType.Count == 0)
            {
                return true;
            }

            if (!Guid.TryParse(item.SourceItemId, out var sourceItemGuid))
            {
                Logger.LogWarning("Invalid source item ID for image sync: {SourceItemId}", item.SourceItemId);
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
                    Logger.LogWarning("Unknown image type: {ImageType}", imageTypeName);
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
                                Logger.LogWarning("Failed to download image {ImageType}/{Index} for {ItemName}",
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
                            Logger.LogWarning(ex, "Error downloading {ImageType}/{Index} image for {ItemName}",
                                imageTypeName, i, localItem.Name);
                            allDownloadsSucceeded = false;
                        }
                    }

                    // Only replace existing images if ALL downloads succeeded for this type.
                    // Partial success would delete existing images and replace with fewer — data loss.
                    if (!allDownloadsSucceeded)
                    {
                        Logger.LogWarning(
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
                            Logger.LogDebug("Removed existing {ImageType} image for {ItemName}", imageTypeName, localItem.Name);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning(ex, "Failed to remove existing {ImageType} image for {ItemName}", imageTypeName, localItem.Name);
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
                            Logger.LogDebug("Saved {ImageType} image for {ItemName}", imageTypeName, localItem.Name);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning(ex, "Error saving {ImageType}/{Index} image for {ItemName}",
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
            Logger.LogError(ex, "Failed to apply images for {ItemName}", item.ItemName);
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
        if (string.IsNullOrEmpty(item.People.Source))
        {
            return true; // Nothing to apply
        }

        try
        {
            var peopleList = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(item.People.Source);
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

            // Some BaseItem subclasses silently no-op UpdatePeople when
            // SupportsPeople == false (e.g. certain folder types). If we
            // hit that, return false so the row is recorded as Errored
            // rather than falsely marked Synced.
            if (!localItem.SupportsPeople)
            {
                Logger.LogWarning(
                    "Cannot sync people for {ItemName} ({ItemType}): SupportsPeople is false on this item type. Local item id {LocalItemId}",
                    item.ItemName,
                    localItem.GetType().Name,
                    localItem.Id);
                return false;
            }

            // Persist the item first so its row is in a clean MetadataEdit
            // state, then write people, then save the item again. Calling
            // UpdatePeople before the item save can leave the people-link
            // table referencing a stale base-item state on some Jellyfin
            // builds. Using the async variant avoids the .GetAwaiter().GetResult()
            // wrapper of the sync version, which can stall under contention.
            await _libraryManager.UpdatePeopleAsync(localItem, people, cancellationToken).ConfigureAwait(false);
            await localItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

            // Verify the write actually persisted. If the item type doesn't
            // accept manual people writes (e.g. they get re-aggregated from
            // children on Series), GetPeople will still return the previous
            // empty/different set after the call. Catch that explicitly so
            // we don't mark a row Synced when local state didn't change.
            var persisted = _libraryManager.GetPeople(localItem);
            if (persisted == null || persisted.Count != people.Count)
            {
                Logger.LogWarning(
                    "Apply people for {ItemName} ({ItemType}, local id {LocalItemId}) wrote {Wrote} entries but {Found} are now linked. The item type may not accept direct people writes (e.g. Series-level people are aggregated from episodes).",
                    item.ItemName,
                    localItem.GetType().Name,
                    localItem.Id,
                    people.Count,
                    persisted?.Count ?? 0);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to apply people for {ItemName}", item.ItemName);
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
        if (string.IsNullOrEmpty(item.Studios.Source))
        {
            return true; // Nothing to apply
        }

        try
        {
            var studiosList = JsonSerializer.Deserialize<List<string>>(item.Studios.Source);
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
            Logger.LogError(ex, "Failed to apply studios for {ItemName}", item.ItemName);
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

}
