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
using Jellyfin.Plugin.ServerSync.Utilities;
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
/// passes a post-write verification read is per-category <c>MarkSynced</c>'d
/// so its hash short-circuit is honored on the next Refresh; a category
/// that fails (apply error or verification mismatch) causes the row to
/// transition to <see cref="SyncStatus.Errored"/> with a <see cref="SyncRecord.Reason"/>
/// naming the diverging category and cause.
/// </summary>
public class SyncMissingMetadataTask : SyncQueueTaskBase<MetadataSyncItem, (string SourceLibraryId, string SourceItemId)>
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly MetadataSyncTableService _metadataService;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public SyncMissingMetadataTask(
        ILogger<SyncMissingMetadataTask> logger,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        ISourceServerClientFactory clientFactory,
        IPluginConfigurationManager configManager,
        MetadataSyncTableService metadataService,
        MetadataSyncTableManager manager)
        : base(logger, manager, clientFactory, configManager)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _metadataService = metadataService;
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
    /// <remarks>
    /// Image downloads dominate per-item wall time (HTTP round-trip per
    /// image type, then a file write); without parallelism a few thousand
    /// items take 8+ minutes serially. Local writes (UpdatePeopleAsync,
    /// UpdateToRepositoryAsync, IProviderManager.SaveImage) go through
    /// Jellyfin's repository which has its own internal locking, so
    /// concurrent applies serialize cleanly at persist time while still
    /// overlapping the HTTP-bound download phase.
    /// </remarks>
    protected override int MaxDegreeOfParallelism => 4;

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

        // Phase 1: write per-category. Each apply mutates localItem in-memory
        // and reports whether anything changed. The composite UpdateToRepository
        // call combines flags so we save once per item, not once per category.
        var metadataChanged = false;
        var imagesChanged = false;
        var peopleChanged = false;
        var studiosChanged = false;
        var anyApplyAttempted = false;

        if (config.MetadataSyncMetadata && record.HasMetadataChanges)
        {
            anyApplyAttempted = true;
            try
            {
                metadataChanged = ApplyMetadata(localItem, record, config.MetadataSyncGenres, config.MetadataSyncTags);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "Metadata apply threw for {ItemName}", record.ItemName);
                failures.Add($"Metadata: apply threw — {ex.Message}");
            }
        }

        if (config.MetadataSyncImages && record.HasImagesChanges)
        {
            anyApplyAttempted = true;
            try
            {
                imagesChanged = await ApplyImagesAsync(localItem, record, Client, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Images apply threw for {ItemName}", record.ItemName);
                failures.Add($"Images: apply threw — {ex.Message}");
            }
        }

        if (config.MetadataSyncPeople && record.HasPeopleChanges)
        {
            anyApplyAttempted = true;
            if (!localItem.SupportsPeople)
            {
                failures.Add($"People: item type {localItem.GetType().Name} does not accept manual people writes");
                Logger.LogWarning(
                    "Cannot sync people for {ItemName} ({ItemType}): SupportsPeople is false. Local id {LocalItemId}",
                    record.ItemName,
                    localItem.GetType().Name,
                    localItem.Id);
            }
            else
            {
                try
                {
                    peopleChanged = await ApplyPeopleAsync(localItem, record, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "People apply threw for {ItemName}", record.ItemName);
                    failures.Add($"People: apply threw — {ex.Message}");
                }
            }
        }

        if (config.MetadataSyncStudios && record.HasStudiosChanges)
        {
            anyApplyAttempted = true;
            try
            {
                studiosChanged = ApplyStudios(localItem, record);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "Studios apply threw for {ItemName}", record.ItemName);
                failures.Add($"Studios: apply threw — {ex.Message}");
            }
        }

        // Phase 2: persist. One UpdateToRepositoryAsync with combined flags.
        // ApplyImagesAsync already called UpdateToRepositoryAsync(ImageUpdate)
        // because SaveImage internally requires the item to have a row, so we
        // only need to combine the field-mutation flag here.
        if (anyApplyAttempted && (metadataChanged || peopleChanged || studiosChanged))
        {
            try
            {
                await localItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "UpdateToRepositoryAsync(MetadataEdit) threw for {ItemName}", record.ItemName);
                failures.Add($"Persist: UpdateToRepositoryAsync threw — {ex.Message}");
            }
        }

        // Phase 3: verify. Re-read the local item and confirm the write took.
        // Verification failures don't throw — they just prevent MarkSynced and
        // are recorded as Errored with a precise Reason.
        if (failures.Count == 0 && anyApplyAttempted)
        {
            // Re-read so we get whatever Jellyfin actually persisted, including
            // any provider/aggregation logic that may have rewritten our blob.
            var freshLocal = _libraryManager.GetItemById(localItemId);
            if (freshLocal == null)
            {
                failures.Add("Verify: local item disappeared after apply");
            }
            else
            {
                if (config.MetadataSyncMetadata && record.HasMetadataChanges)
                {
                    var (ok, reason) = VerifyMetadataApplied(freshLocal, record, config.MetadataSyncGenres, config.MetadataSyncTags);
                    if (ok)
                    {
                        record.Metadata.MarkSynced();
                        synced.Add("Metadata");
                        Logger.LogInformation("Apply Metadata verified for {ItemName}", record.ItemName);
                    }
                    else
                    {
                        failures.Add($"Metadata: {reason}");
                        Logger.LogWarning("Apply Metadata failed verification for {ItemName}: {Reason}", record.ItemName, reason);
                    }
                }

                if (config.MetadataSyncImages && record.HasImagesChanges)
                {
                    var (ok, reason) = VerifyImagesApplied(freshLocal, record);
                    if (ok)
                    {
                        record.Images.MarkSynced();
                        synced.Add("Images");
                        Logger.LogInformation("Apply Images verified for {ItemName}", record.ItemName);
                    }
                    else
                    {
                        failures.Add($"Images: {reason}");
                        Logger.LogWarning("Apply Images failed verification for {ItemName}: {Reason}", record.ItemName, reason);
                    }
                }

                if (config.MetadataSyncPeople && record.HasPeopleChanges && localItem.SupportsPeople)
                {
                    var (ok, reason) = VerifyPeopleApplied(freshLocal, record);
                    if (ok)
                    {
                        record.People.MarkSynced();
                        synced.Add("People");
                        Logger.LogInformation("Apply People verified for {ItemName}", record.ItemName);
                    }
                    else
                    {
                        failures.Add($"People: {reason}");
                        Logger.LogWarning("Apply People failed verification for {ItemName}: {Reason}", record.ItemName, reason);
                    }
                }

                if (config.MetadataSyncStudios && record.HasStudiosChanges)
                {
                    var (ok, reason) = VerifyStudiosApplied(freshLocal, record);
                    if (ok)
                    {
                        record.Studios.MarkSynced();
                        synced.Add("Studios");
                        Logger.LogInformation("Apply Studios verified for {ItemName}", record.ItemName);
                    }
                    else
                    {
                        failures.Add($"Studios: {reason}");
                        Logger.LogWarning("Apply Studios failed verification for {ItemName}: {Reason}", record.ItemName, reason);
                    }
                }
            }
        }

        // Suppress unused-variable warnings — we tracked the changed flags so
        // the verification log line below has consistent context.
        _ = metadataChanged;
        _ = imagesChanged;
        _ = peopleChanged;
        _ = studiosChanged;

        if (failures.Count > 0)
        {
            // Categories that succeeded keep their per-category MarkSynced
            // state on the record — the base class will Upsert the record
            // as Errored, persisting the partial progress so the next run
            // doesn't redo the categories that already succeeded.
            throw new InvalidOperationException(string.Join("; ", failures));
        }

        if (synced.Count > 0)
        {
            Logger.LogDebug("Synced {Categories} for {ItemName}", string.Join(", ", synced), record.ItemName);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// No-op — per-category <see cref="SyncableValue{T}.MarkSynced"/> calls
    /// already happened inside <see cref="ApplyAsync"/> for the categories
    /// whose verify passed.
    /// <para>
    /// Calling <see cref="MetadataSyncItem.MarkSynced"/> here would set
    /// <c>SyncedHash = SourceHash</c> on categories that this run did NOT
    /// actually apply (skipped because per-category <c>HasChanges</c> was
    /// false at apply time, or skipped because the user's config disabled
    /// that category, or skipped because the row was force-queued from the
    /// UI without a matching source change). The next Refresh would then
    /// see <c>SourceHash == SyncedHash</c>, short-circuit
    /// <see cref="SyncableValue{T}.HasChanges"/> to false, and
    /// <c>DecideStatus</c> would mark the row Synced — even when local
    /// genuinely diverges from source. The UI keeps showing a diff (it
    /// compares Source vs Local directly), but the apply phase only ever
    /// processes Queued rows, so pressing Sync does nothing. That broke
    /// images, people, and metadata across the board in 10.11.54.
    /// </para>
    /// </remarks>
    protected override void OnApplySucceeded(MetadataSyncItem record)
    {
        // Intentionally empty — see remarks.
        _ = record;
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
    /// Applies metadata fields to the local item, writing nulls and empty
    /// arrays through to local so "remove a tag" / "clear an overview" on
    /// source actually clears local. Does NOT call
    /// <see cref="MediaBrowser.Controller.Entities.BaseItem.UpdateToRepositoryAsync"/> —
    /// the caller batches all category writes into a single repository save.
    /// </summary>
    /// <returns>True if anything was changed, false if no-op.</returns>
    private bool ApplyMetadata(
        MediaBrowser.Controller.Entities.BaseItem localItem,
        MetadataSyncItem item,
        bool syncGenres,
        bool syncTags)
    {
        if (string.IsNullOrEmpty(item.Metadata.Source))
        {
            return false;
        }

        var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.Metadata.Source);
        if (metadata == null)
        {
            throw new InvalidOperationException("Metadata source blob deserialized to null");
        }

        var hasChanges = false;
        var localVideo = localItem as MediaBrowser.Controller.Entities.Video;

        // Strings — assign through nulls and empty strings so source clearing
        // a field actually clears local.
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "Name", v => { if (!string.IsNullOrEmpty(v) && localItem.Name != v) { localItem.Name = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "OriginalTitle", v => { if (localItem.OriginalTitle != v) { localItem.OriginalTitle = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "Overview", v => { if (localItem.Overview != v) { localItem.Overview = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "OfficialRating", v => { if (localItem.OfficialRating != v) { localItem.OfficialRating = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "Tagline", v => { if (localItem.Tagline != v) { localItem.Tagline = v; return true; } return false; });
        // SortName intentionally not synced: Jellyfin derives SortName
        // from Name independently on each server, so writing it from
        // source produces a value that local will overwrite on next
        // metadata refresh anyway. ForcedSortName (user override) IS synced.
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "ForcedSortName", v => { if (localItem.ForcedSortName != v) { localItem.ForcedSortName = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "CustomRating", v => { if (localItem.CustomRating != v) { localItem.CustomRating = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "PreferredMetadataCountryCode", v => { if (localItem.PreferredMetadataCountryCode != v) { localItem.PreferredMetadataCountryCode = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "PreferredMetadataLanguage", v => { if (localItem.PreferredMetadataLanguage != v) { localItem.PreferredMetadataLanguage = v; return true; } return false; });

        // Floats — number or null; treat absent as no-op, present-and-null as
        // clear-local.
        hasChanges |= JsonFieldHelpers.AssignFloat(metadata, "CommunityRating", v => { if (localItem.CommunityRating != v) { localItem.CommunityRating = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignFloat(metadata, "CriticRating", v => { if (localItem.CriticRating != v) { localItem.CriticRating = v; return true; } return false; });

        // Ints — same semantic.
        hasChanges |= JsonFieldHelpers.AssignInt(metadata, "ProductionYear", v => { if (localItem.ProductionYear != v) { localItem.ProductionYear = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignInt(metadata, "IndexNumber", v => { if (localItem.IndexNumber != v) { localItem.IndexNumber = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignInt(metadata, "ParentIndexNumber", v => { if (localItem.ParentIndexNumber != v) { localItem.ParentIndexNumber = v; return true; } return false; });

        // Dates — date-only compare.
        if (metadata.TryGetValue("PremiereDate", out var premiereValue))
        {
            var d = JsonFieldHelpers.ParseNullableDate(premiereValue);
            if (!JsonComparisonUtility.DateOnlyEquals(localItem.PremiereDate, d))
            {
                localItem.PremiereDate = d;
                hasChanges = true;
            }
        }

        if (metadata.TryGetValue("EndDate", out var endValue))
        {
            var d = JsonFieldHelpers.ParseNullableDate(endValue);
            if (!JsonComparisonUtility.DateOnlyEquals(localItem.EndDate, d))
            {
                localItem.EndDate = d;
                hasChanges = true;
            }
        }

        // Arrays — empty/null source clears local.
        if (syncGenres && metadata.TryGetValue("Genres", out var genresValue))
        {
            var newGenres = JsonFieldHelpers.ReadStringArray(genresValue);
            if (!ArraysEqualOrdinal(localItem.Genres, newGenres))
            {
                localItem.Genres = newGenres;
                hasChanges = true;
            }
        }

        if (syncTags && metadata.TryGetValue("Tags", out var tagsValue))
        {
            var newTags = JsonFieldHelpers.ReadStringArray(tagsValue);
            if (!ArraysEqualOrdinal(localItem.Tags, newTags))
            {
                localItem.Tags = newTags;
                hasChanges = true;
            }
        }

        // ProviderIds: reconcile so local matches source exactly. Includes
        // removing local-only keys.
        if (metadata.TryGetValue("ProviderIds", out var providerIdsValue))
        {
            var sourceIds = JsonFieldHelpers.ReadProviderIds(providerIdsValue);
            // Remove local-only entries first.
            if (localItem.ProviderIds != null)
            {
                var toRemove = localItem.ProviderIds.Keys
                    .Where(k => !sourceIds.ContainsKey(k))
                    .ToList();
                foreach (var key in toRemove)
                {
                    localItem.ProviderIds.Remove(key);
                    hasChanges = true;
                }
            }

            foreach (var kvp in sourceIds)
            {
                if (localItem.GetProviderId(kvp.Key) != kvp.Value)
                {
                    localItem.SetProviderId(kvp.Key, kvp.Value);
                    hasChanges = true;
                }
            }
        }

        // Video-specific fields. Settable only when local is a Video.
        if (localVideo != null)
        {
            hasChanges |= JsonFieldHelpers.AssignString(metadata, "AspectRatio", v => { if (localVideo.AspectRatio != v) { localVideo.AspectRatio = v; return true; } return false; });

            if (metadata.TryGetValue("Video3DFormat", out var video3DValue))
            {
                Video3DFormat? format = null;
                if (video3DValue.ValueKind == JsonValueKind.String)
                {
                    var s = video3DValue.GetString();
                    if (!string.IsNullOrEmpty(s) && Enum.TryParse<Video3DFormat>(s, out var parsed))
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
        }

        // LockedFields — empty source clears local.
        if (metadata.TryGetValue("LockedFields", out var lockedValue))
        {
            var newLocked = JsonFieldHelpers.ReadEnumArray<MetadataField>(lockedValue);
            if (!ArraysEqual(localItem.LockedFields, newLocked))
            {
                localItem.LockedFields = newLocked;
                hasChanges = true;
            }
        }

        // LockData — bool? coalesced to false when null, matching source-side
        // serialization (which writes `LockData ?? false`).
        if (metadata.TryGetValue("LockData", out var lockDataValue))
        {
            bool target = lockDataValue.ValueKind == JsonValueKind.True;
            if (localItem.IsLocked != target)
            {
                localItem.IsLocked = target;
                hasChanges = true;
            }
        }

        return hasChanges;
    }

    /// <summary>
    /// Applies images from the source server to a local item.
    /// Two-pass approach: first download to temp files (so a partial
    /// failure leaves existing local images intact), then delete + replace
    /// per type. Calls <see cref="MediaBrowser.Controller.Entities.BaseItem.UpdateToRepositoryAsync"/>
    /// at the end because <see cref="IProviderManager.SaveImage"/> needs the
    /// item present in the repository.
    /// </summary>
    private async Task<bool> ApplyImagesAsync(
        MediaBrowser.Controller.Entities.BaseItem localItem,
        MetadataSyncItem item,
        SourceServerClient? sourceClient,
        CancellationToken cancellationToken)
    {
        if (sourceClient == null)
        {
            throw new InvalidOperationException("No source server client available for image sync");
        }

        if (string.IsNullOrEmpty(item.Images.Source))
        {
            return false;
        }

        var sourceImagesByType = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(item.Images.Source);
        if (sourceImagesByType == null || sourceImagesByType.Count == 0)
        {
            return false;
        }

        if (!Guid.TryParse(item.SourceItemId, out var sourceItemGuid))
        {
            throw new InvalidOperationException($"Invalid source item ID for image sync: {item.SourceItemId}");
        }

        var anyChanged = false;
        // Per-type failure log. If non-empty after the loop, we throw at the
        // end so the orchestrator records this category as Errored with the
        // diagnostic. Errors ARE logged inline (LogWarning) for live tailing,
        // but the throw ensures verification doesn't silently mark Synced
        // when only some types/indexes landed.
        var perTypeErrors = new List<string>();

        foreach (var kvp in sourceImagesByType)
        {
            var imageTypeName = kvp.Key;
            var sourceImages = kvp.Value;

            if (!Enum.TryParse<ImageType>(imageTypeName, out var imageType))
            {
                Logger.LogWarning("Unknown image type: {ImageType}", imageTypeName);
                perTypeErrors.Add($"{imageTypeName}: unknown image type");
                continue;
            }

            // Pass 1: download to temp files. Per-image failures mark the
            // type as failed without aborting other types — the user might
            // still want backdrops even if a thumb failed.
            var tempFiles = new List<(int Index, string TempPath, string ContentType)>();
            var typeErrors = new List<string>();
            try
            {
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
                            typeErrors.Add($"download index {i}: returned null");
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
                        catch (Exception)
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
                        typeErrors.Add($"download index {i}: {ex.Message}");
                    }
                }

                // Per-type atomicity: if ANY download for this type failed,
                // skip Pass 2 entirely. Don't half-replace the local images
                // — user keeps their existing set instead of being left in
                // a half-deleted state. The row will go Errored at the end.
                if (typeErrors.Count > 0)
                {
                    Logger.LogWarning(
                        "Not all {ImageType} downloads succeeded for {ItemName} ({Downloaded}/{Total}), keeping existing local images",
                        imageTypeName, localItem.Name, tempFiles.Count, sourceImages.Count);
                    perTypeErrors.Add($"{imageTypeName}: {string.Join("; ", typeErrors)} (kept existing local images)");
                    continue;
                }

                if (tempFiles.Count == 0)
                {
                    // Source had this type but every download was empty —
                    // nothing to do, nothing failed.
                    continue;
                }

                // Pass 2: delete existing + apply, all-or-nothing per type.
                // If a remove or save fails, any partial mutation already
                // made stays (we can't unwind a RemoveImage), but we log the
                // type as failed so verification + user reason are honest.
                var existingImages = localItem.GetImages(imageType).ToList();
                foreach (var existingImage in existingImages)
                {
                    try
                    {
                        localItem.RemoveImage(existingImage);
                        anyChanged = true;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Failed to remove existing {ImageType} image for {ItemName}", imageTypeName, localItem.Name);
                        typeErrors.Add($"remove existing: {ex.Message}");
                    }
                }

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
                        anyChanged = true;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Error saving {ImageType}/{Index} image for {ItemName}",
                            imageTypeName, index, localItem.Name);
                        typeErrors.Add($"save index {index}: {ex.Message}");
                    }
                }

                if (typeErrors.Count > 0)
                {
                    perTypeErrors.Add($"{imageTypeName}: {string.Join("; ", typeErrors)}");
                }
            }
            finally
            {
                foreach (var (_, tempPath, _) in tempFiles)
                {
                    try { File.Delete(tempPath); } catch (IOException) { }
                }
            }
        }

        if (anyChanged)
        {
            await localItem.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
        }

        // Surface any per-type failures so the row goes Errored with a
        // precise reason. Without this, half-applied image state would
        // pass MarkSynced and lie about success.
        if (perTypeErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Image apply incomplete for {item.ItemName}: {string.Join(" | ", perTypeErrors)}");
        }

        return anyChanged;
    }

    /// <summary>
    /// Applies people. Does NOT call UpdateToRepositoryAsync — caller batches.
    /// </summary>
    private async Task<bool> ApplyPeopleAsync(
        MediaBrowser.Controller.Entities.BaseItem localItem,
        MetadataSyncItem item,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.People.Source))
        {
            return false;
        }

        var peopleList = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(item.People.Source);
        if (peopleList == null)
        {
            throw new InvalidOperationException("People source blob deserialized to null");
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

        await _libraryManager.UpdatePeopleAsync(localItem, people, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Applies studios. Does NOT call UpdateToRepositoryAsync — caller batches.
    /// </summary>
    private bool ApplyStudios(
        MediaBrowser.Controller.Entities.BaseItem localItem,
        MetadataSyncItem item)
    {
        if (string.IsNullOrEmpty(item.Studios.Source))
        {
            return false;
        }

        var studiosList = JsonSerializer.Deserialize<List<string>>(item.Studios.Source);
        if (studiosList == null)
        {
            throw new InvalidOperationException("Studios source blob deserialized to null");
        }

        var newStudios = studiosList.ToArray();
        if (ArraysEqualOrdinal(localItem.Studios, newStudios))
        {
            return false;
        }

        localItem.Studios = newStudios;
        return true;
    }

    // ===================================================================
    // Verification — re-read local state and compare against source blob.
    // Each helper is a pure function for testability: takes the freshly-
    // read local item + source record, returns (succeeded, failureReason).
    // ===================================================================

    private (bool Succeeded, string? FailureReason) VerifyMetadataApplied(
        MediaBrowser.Controller.Entities.BaseItem freshLocal,
        MetadataSyncItem record,
        bool syncGenres,
        bool syncTags)
    {
        // Rebuild the freshly-extracted local blob using the same path the
        // refresh would use, then compare to the source blob. JsonEquals is
        // tolerant of property ordering and date-format jitter.
        _metadataService.RefreshLocalSnapshot(record, syncMetadata: true, syncImages: false, syncPeople: false, syncStudios: false, syncGenres: syncGenres, syncTags: syncTags);
        var fresh = record.Metadata.Local;
        var source = record.Metadata.Source;
        if (JsonComparisonUtility.JsonEquals(fresh, source))
        {
            return (true, null);
        }

        var differingFields = JsonComparisonUtility.GetDifferingFields(source, fresh);
        var fieldList = differingFields.Count == 0 ? "(none)" : string.Join(",", differingFields);
        var detail = JsonComparisonUtility.DescribeDifferingFields(source, fresh);
        return (false, $"verification found {differingFields.Count} divergent field(s) [{fieldList}] (item type {freshLocal.GetType().Name}); {detail}");
    }

    private (bool Succeeded, string? FailureReason) VerifyImagesApplied(
        MediaBrowser.Controller.Entities.BaseItem freshLocal,
        MetadataSyncItem record)
    {
        _ = freshLocal;
        _metadataService.RefreshLocalSnapshot(record, syncMetadata: false, syncImages: true, syncPeople: false, syncStudios: false, syncGenres: false, syncTags: false);
        // Use the same comparator the refresh uses for source==local image
        // diffs (tag-aware). If the comparator says they match, we're synced.
        // When they don't match, ask the comparator for a specific diff so
        // the failure reason names the offending type/index instead of a
        // generic "count or per-type tag/size mismatch".
        var imagesComparator = record.Images.Comparator as Models.Common.Comparators.ImageManifestComparator;
        if (imagesComparator != null)
        {
            // Strip Profile entries from the source side before comparing.
            // Metadata sync only writes to non-Person items, where Jellyfin
            // silently rejects Profile saves; if a record was built before
            // PopulateSourceImagesFromTags learned to filter Profile (or
            // ever drifts back), we'd be stuck reporting an unsyncable diff
            // forever. Strip here so the verify reflects what's actually
            // syncable instead of what's on the wire from source.
            var sanitizedSource = StripUnsyncableImageTypes(record.Images.Source);
            var diff = imagesComparator.DescribeDifference(sanitizedSource, record.Images.Local);
            if (diff == null)
            {
                return (true, null);
            }

            return (false, $"image manifest after apply does not match source manifest: {diff}");
        }

        if (record.Images.Comparator.Equals(record.Images.Source, record.Images.Local))
        {
            return (true, null);
        }

        return (false, "image manifest after apply does not match source manifest (count or per-type tag/size mismatch)");
    }

    private (bool Succeeded, string? FailureReason) VerifyPeopleApplied(
        MediaBrowser.Controller.Entities.BaseItem freshLocal,
        MetadataSyncItem record)
    {
        // Direct count check first — Jellyfin sometimes silently no-ops the
        // people write on item types that aggregate from children (Series).
        var sourceList = ParsePeopleList(record.People.Source);
        var localPeople = _libraryManager.GetPeople(freshLocal);
        if (sourceList == null)
        {
            return (false, "source people blob deserialized to null");
        }

        var localCount = localPeople?.Count ?? 0;
        if (localCount != sourceList.Count)
        {
            return (false, $"wrote {sourceList.Count} entries but {localCount} are now linked on local — Jellyfin overwrote the manual write (item type: {freshLocal.GetType().Name})");
        }

        // Names must match (case-insensitive). Roles vary harmlessly across
        // Jellyfin builds; matching names is enough to confirm persistence.
        var sourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in sourceList)
        {
            if (p.TryGetValue("Name", out var n) && !string.IsNullOrEmpty(n))
            {
                sourceNames.Add(n);
            }
        }

        var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (localPeople != null)
        {
            foreach (var p in localPeople)
            {
                if (!string.IsNullOrEmpty(p.Name))
                {
                    localNames.Add(p.Name);
                }
            }
        }

        if (!sourceNames.SetEquals(localNames))
        {
            return (false, $"name set mismatch: source has {sourceNames.Count}, local has {localNames.Count} after write (item type: {freshLocal.GetType().Name})");
        }

        return (true, null);
    }

    private (bool Succeeded, string? FailureReason) VerifyStudiosApplied(
        MediaBrowser.Controller.Entities.BaseItem freshLocal,
        MetadataSyncItem record)
    {
        var sourceList = JsonSerializer.Deserialize<List<string>>(record.Studios.Source ?? "[]") ?? new List<string>();
        var localStudios = freshLocal.Studios ?? Array.Empty<string>();

        var sourceSet = new HashSet<string>(sourceList, StringComparer.OrdinalIgnoreCase);
        var localSet = new HashSet<string>(localStudios.Where(s => !string.IsNullOrWhiteSpace(s)), StringComparer.OrdinalIgnoreCase);

        if (sourceSet.SetEquals(localSet))
        {
            return (true, null);
        }

        return (false, $"studios mismatch: source has {sourceSet.Count} ({string.Join("|", sourceSet.OrderBy(s => s))}), local has {localSet.Count} ({string.Join("|", localSet.OrderBy(s => s))})");
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
            _ => 2
        };
    }

    // ===================================================================
    // Apply helpers — local extraction utilities.
    // ===================================================================

    /// <summary>
    /// Reads a string value from the dict and passes it to the assigner.
    /// Returns the assigner's result. The value is null when the JSON is
    /// null, the empty string when the JSON is an empty string, or the
    /// content otherwise. If the key is absent, returns false and does not
    /// invoke the assigner.
    /// </summary>
    /// <summary>
    /// Removes image types that the metadata sync apply path can't actually
    /// write to a non-Person item. Currently filters out <c>Profile</c>:
    /// Jellyfin's repository silently drops Profile writes on Movie/Series/
    /// Episode/etc, so a Profile entry in the source manifest can never
    /// stick on local. Stripping it here keeps already-cached records (built
    /// before the source-side filter landed) from getting pinned in Errored
    /// over a divergence that's unsyncable by design.
    /// </summary>
    private static string? StripUnsyncableImageTypes(string? sourceManifest)
    {
        if (string.IsNullOrEmpty(sourceManifest)) return sourceManifest;
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(sourceManifest);
            if (map == null || map.Count == 0) return sourceManifest;
            var removed = map.Keys
                .Where(k => string.Equals(k, "Profile", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (removed.Count == 0) return sourceManifest;
            foreach (var key in removed) map.Remove(key);
            return JsonSerializer.Serialize(map);
        }
        catch (JsonException)
        {
            return sourceManifest;
        }
    }

    private static bool ArraysEqualOrdinal(string[]? a, string[]? b)
    {
        var aArr = a ?? Array.Empty<string>();
        var bArr = b ?? Array.Empty<string>();
        if (aArr.Length != bArr.Length) return false;
        for (var i = 0; i < aArr.Length; i++)
        {
            if (!string.Equals(aArr[i], bArr[i], StringComparison.Ordinal)) return false;
        }

        return true;
    }

    private static bool ArraysEqual<T>(T[]? a, T[]? b) where T : struct
    {
        var aArr = a ?? Array.Empty<T>();
        var bArr = b ?? Array.Empty<T>();
        if (aArr.Length != bArr.Length) return false;
        for (var i = 0; i < aArr.Length; i++)
        {
            if (!aArr[i].Equals(bArr[i])) return false;
        }

        return true;
    }

    private static List<Dictionary<string, string>>? ParsePeopleList(string? blob)
    {
        if (string.IsNullOrEmpty(blob)) return new List<Dictionary<string, string>>();
        try
        {
            return JsonSerializer.Deserialize<List<Dictionary<string, string>>>(blob);
        }
        catch (JsonException)
        {
            return null;
        }
    }

}
