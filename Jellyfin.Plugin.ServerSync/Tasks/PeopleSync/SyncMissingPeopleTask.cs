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
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Sync phase for People. Reads Queued rows and applies metadata + image
/// changes to the local person entities, then verifies the writes by
/// re-reading local state. A category that fails verification is recorded
/// as Errored with a precise <see cref="SyncRecord.Reason"/> rather than
/// being silently marked Synced.
/// </summary>
public class SyncMissingPeopleTask : SyncQueueTaskBase<PeopleSyncItem, string>
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly PeopleSyncTableService _peopleService;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public SyncMissingPeopleTask(
        ILogger<SyncMissingPeopleTask> logger,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        ISourceServerClientFactory clientFactory,
        IPluginConfigurationManager configManager,
        PeopleSyncTableService peopleService,
        PeopleSyncTableManager manager)
        : base(logger, manager, clientFactory, configManager)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _peopleService = peopleService;
    }

    /// <inheritdoc />
    public override string Name => "Sync People Data";

    /// <inheritdoc />
    public override string Key => "ServerSyncMissingPeople";

    /// <inheritdoc />
    public override string Description => "Applies queued people sync changes (metadata, images) to local person entities.";

    /// <inheritdoc />
    public override string Category => "People Sync";

    /// <inheritdoc />
    protected override string ModuleMutexKey => "People";

    /// <inheritdoc />
    protected override bool IsEnabled()
    {
        var config = ConfigManager.Configuration;
        return config.EnablePeopleSync
            && !string.IsNullOrWhiteSpace(config.SourceServerUrl)
            && !string.IsNullOrWhiteSpace(config.SourceServerApiKey);
    }

    /// <inheritdoc />
    protected override async Task ApplyAsync(PeopleSyncItem record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var personStub = _libraryManager.GetPerson(record.PersonName);
        if (personStub == null)
        {
            throw new InvalidOperationException($"Local person not found: {record.PersonName}");
        }

        var localPerson = _libraryManager.GetItemById(personStub.Id);
        if (localPerson == null)
        {
            throw new InvalidOperationException($"Could not load person entity: {record.PersonName}");
        }

        var config = ConfigManager.Configuration;
        var syncImages = config.PeopleSyncImages;
        var failures = new List<string>();
        var synced = new List<string>();

        var metadataChanged = false;
        var imagesChanged = false;
        var anyApplyAttempted = false;

        if (record.HasMetadataChanges)
        {
            anyApplyAttempted = true;
            try
            {
                metadataChanged = ApplyMetadata(localPerson, record);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "Metadata apply threw for {PersonName}", record.PersonName);
                failures.Add($"Metadata: apply threw — {ex.Message}");
            }
        }

        if (syncImages && record.HasImagesChanges && !string.IsNullOrEmpty(record.SourcePersonId))
        {
            if (!Guid.TryParse(record.SourcePersonId, out var sourceGuid))
            {
                failures.Add($"Images: invalid source person ID {record.SourcePersonId}");
            }
            else if (Client == null)
            {
                failures.Add("Images: source server client unavailable");
            }
            else
            {
                anyApplyAttempted = true;
                try
                {
                    imagesChanged = await ApplyPersonImagesAsync(localPerson, sourceGuid, record, Client, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Images apply threw for {PersonName}", record.PersonName);
                    failures.Add($"Images: apply threw — {ex.Message}");
                }
            }
        }

        // Single combined repository save for any field-level mutations.
        if (failures.Count == 0 && anyApplyAttempted && metadataChanged)
        {
            try
            {
                await localPerson.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "UpdateToRepositoryAsync threw for {PersonName}", record.PersonName);
                failures.Add($"Persist: UpdateToRepositoryAsync threw — {ex.Message}");
            }
        }

        // Verify what actually persisted.
        if (failures.Count == 0 && anyApplyAttempted)
        {
            var freshPerson = _libraryManager.GetItemById(personStub.Id);
            if (freshPerson == null)
            {
                failures.Add("Verify: person entity disappeared after apply");
            }
            else
            {
                if (record.HasMetadataChanges)
                {
                    var (ok, reason) = VerifyMetadataApplied(freshPerson, record);
                    if (ok)
                    {
                        record.Metadata.MarkSynced();
                        synced.Add("Metadata");
                        Logger.LogInformation("Apply Metadata verified for person {PersonName}", record.PersonName);
                    }
                    else
                    {
                        failures.Add($"Metadata: {reason}");
                        Logger.LogWarning("Apply Metadata failed verification for person {PersonName}: {Reason}", record.PersonName, reason);
                    }
                }

                if (syncImages && record.HasImagesChanges)
                {
                    var (ok, reason) = VerifyImagesApplied(freshPerson, record);
                    if (ok)
                    {
                        record.Images.MarkSynced();
                        synced.Add("Images");
                        Logger.LogInformation("Apply Images verified for person {PersonName}", record.PersonName);
                    }
                    else
                    {
                        failures.Add($"Images: {reason}");
                        Logger.LogWarning("Apply Images failed verification for person {PersonName}: {Reason}", record.PersonName, reason);
                    }
                }
            }
        }

        _ = metadataChanged;
        _ = imagesChanged;

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", failures));
        }

        if (synced.Count > 0)
        {
            Logger.LogDebug("Synced {Categories} for person {PersonName}", string.Join(", ", synced), record.PersonName);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Normalize all per-category SyncedHashes to current SourceHashes on
    /// successful apply. Per-category MarkSynced runs inside ApplyAsync
    /// for categories that actually applied; this covers the categories
    /// that were skipped because no work was needed at apply time. Without
    /// this, a row Refresh queued (because one category transiently looked
    /// diverged) but Sync left untouched (because all categories agreed at
    /// apply time) keeps stale SyncedHashes — the next Refresh re-queues
    /// it, looping forever.
    /// </remarks>
    protected override void OnApplySucceeded(PeopleSyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.MarkSynced();
    }

    /// <inheritdoc />
    protected override Task FinalizeAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ConfigManager.Configuration.LastPeopleSyncTime = DateTime.UtcNow;
        ConfigManager.SaveConfiguration();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(14).Ticks
        }
    };

    /// <summary>
    /// Applies metadata fields to the local person, writing nulls and empty
    /// arrays through to local. Caller batches the repository save.
    /// </summary>
    private bool ApplyMetadata(BaseItem localPerson, PeopleSyncItem item)
    {
        if (string.IsNullOrEmpty(item.Metadata.Source))
        {
            return false;
        }

        var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.Metadata.Source)
            ?? throw new InvalidOperationException("Person metadata source blob deserialized to null");

        var hasChanges = false;

        // Strings — assign through nulls.
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "Name", v => { if (!string.IsNullOrEmpty(v) && localPerson.Name != v) { localPerson.Name = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "OriginalTitle", v => { if (localPerson.OriginalTitle != v) { localPerson.OriginalTitle = v; return true; } return false; });
        // SortName intentionally not synced — Jellyfin derives it from
        // Name independently per server, so cross-server writes never
        // stick. ForcedSortName (user override) IS synced.
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "ForcedSortName", v => { if (localPerson.ForcedSortName != v) { localPerson.ForcedSortName = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "Overview", v => { if (localPerson.Overview != v) { localPerson.Overview = v; return true; } return false; });

        // Dates — date-only compare.
        if (metadata.TryGetValue("PremiereDate", out var premiereValue))
        {
            var d = JsonFieldHelpers.ParseNullableDate(premiereValue);
            if (!JsonComparisonUtility.DateOnlyEquals(localPerson.PremiereDate, d))
            {
                localPerson.PremiereDate = d;
                hasChanges = true;
            }
        }

        if (metadata.TryGetValue("EndDate", out var endValue))
        {
            var d = JsonFieldHelpers.ParseNullableDate(endValue);
            if (!JsonComparisonUtility.DateOnlyEquals(localPerson.EndDate, d))
            {
                localPerson.EndDate = d;
                hasChanges = true;
            }
        }

        // Ints
        if (metadata.TryGetValue("ProductionYear", out var yearValue))
        {
            int? year = yearValue.ValueKind == JsonValueKind.Number ? yearValue.GetInt32() : null;
            if (localPerson.ProductionYear != year)
            {
                localPerson.ProductionYear = year;
                hasChanges = true;
            }
        }

        // Arrays — empty/null source clears local.
        if (metadata.TryGetValue("ProductionLocations", out var locationsValue))
        {
            var newLocations = JsonFieldHelpers.ReadStringArray(locationsValue);
            var current = localPerson.ProductionLocations ?? Array.Empty<string>();
            if (!current.SequenceEqual(newLocations, StringComparer.Ordinal))
            {
                localPerson.ProductionLocations = newLocations;
                hasChanges = true;
            }
        }

        if (metadata.TryGetValue("Tags", out var tagsValue))
        {
            var newTags = JsonFieldHelpers.ReadStringArray(tagsValue);
            var current = localPerson.Tags ?? Array.Empty<string>();
            if (!current.SequenceEqual(newTags, StringComparer.Ordinal))
            {
                localPerson.Tags = newTags;
                hasChanges = true;
            }
        }

        // ProviderIds reconcile.
        if (metadata.TryGetValue("ProviderIds", out var providerIdsValue))
        {
            var sourceIds = JsonFieldHelpers.ReadProviderIds(providerIdsValue);
            if (localPerson.ProviderIds != null)
            {
                var toRemove = localPerson.ProviderIds.Keys.Where(k => !sourceIds.ContainsKey(k)).ToList();
                foreach (var key in toRemove)
                {
                    localPerson.ProviderIds.Remove(key);
                    hasChanges = true;
                }
            }

            foreach (var kvp in sourceIds)
            {
                if (localPerson.GetProviderId(kvp.Key) != kvp.Value)
                {
                    localPerson.SetProviderId(kvp.Key, kvp.Value);
                    hasChanges = true;
                }
            }
        }

        // LockedFields — empty source clears local.
        if (metadata.TryGetValue("LockedFields", out var lockedValue))
        {
            var newLocked = JsonFieldHelpers.ReadEnumArray<MetadataField>(lockedValue);
            var current = localPerson.LockedFields ?? Array.Empty<MetadataField>();
            if (!current.SequenceEqual(newLocked))
            {
                localPerson.LockedFields = newLocked;
                hasChanges = true;
            }
        }

        // LockData — coalesce null to false.
        if (metadata.TryGetValue("LockData", out var lockDataValue))
        {
            bool target = lockDataValue.ValueKind == JsonValueKind.True;
            if (localPerson.IsLocked != target)
            {
                localPerson.IsLocked = target;
                hasChanges = true;
            }
        }

        return hasChanges;
    }

    private async Task<bool> ApplyPersonImagesAsync(
        BaseItem localPerson,
        Guid sourcePersonId,
        PeopleSyncItem record,
        SourceServerClient imageClient,
        CancellationToken cancellationToken)
    {
        // Build the list of (type, index) tuples to download. Prefer the
        // info call (it gives us per-image dimensions for richer logging),
        // but fall back to the source manifest we already cached during
        // refresh when the info call comes back empty. On Jellyfin source
        // servers we sometimes see /Items/{personId}/Images return zero
        // entries even though /Persons reported ImageTags for that person —
        // appears to be an indexing inconsistency on the source side.
        // Without this fallback, the apply was a no-op for ~15 records per
        // run, leaving them pinned in Errored forever despite source clearly
        // having an image.
        var work = new List<(string ImageType, int? ImageIndex)>();
        var sourceImages = await imageClient.GetItemImageInfoAsync(sourcePersonId, cancellationToken).ConfigureAwait(false);
        var fellBack = false;
        if (sourceImages != null && sourceImages.Count > 0)
        {
            foreach (var info in sourceImages)
            {
                var t = info.ImageType?.ToString();
                if (!string.IsNullOrEmpty(t))
                {
                    work.Add((t, info.ImageIndex));
                }
            }
        }

        if (work.Count == 0)
        {
            // Fall back to the cached ImageTags-derived manifest. Type and
            // index are all the download endpoint needs.
            if (!string.IsNullOrEmpty(record.Images.Source))
            {
                try
                {
                    var manifest = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(record.Images.Source);
                    if (manifest != null)
                    {
                        foreach (var (type, list) in manifest)
                        {
                            if (string.IsNullOrEmpty(type) || list == null) continue;
                            foreach (var entry in list)
                            {
                                work.Add((type, entry.ImageIndex));
                            }
                        }

                        if (work.Count > 0)
                        {
                            fellBack = true;
                            Logger.LogInformation(
                                "Image apply for {PersonName}: /Items/{Id}/Images returned no entries; falling back to cached ImageTags manifest with {Count} type(s)",
                                record.PersonName, sourcePersonId, work.Count);
                        }
                    }
                }
                catch (JsonException ex)
                {
                    Logger.LogWarning(ex, "Could not parse cached image manifest for {PersonName}; falling back to no-op", record.PersonName);
                }
            }
        }

        if (work.Count == 0)
        {
            return false;
        }

        // Clear local images for any type we're about to write, so a source
        // count of 1 doesn't leave behind extra local images and produce a
        // "source has 1, local has 2" verify failure (e.g. when local picked
        // up a second Primary from a prior provider scrape). RemoveImage
        // mutates the in-memory ImageInfos list; call UpdateToRepositoryAsync
        // afterward so the repo reflects the cleared state before SaveImage
        // runs (without this, SaveImage's own DB write doesn't see our
        // removals and the old rows survive).
        var typesBeingWritten = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (t, _) in work) typesBeingWritten.Add(t);
        var anyRemoved = false;
        foreach (var typeName in typesBeingWritten)
        {
            if (Enum.TryParse<ImageType>(typeName, true, out var parsed))
            {
                var existing = localPerson.GetImages(parsed).ToList();
                foreach (var img in existing)
                {
                    try
                    {
                        localPerson.RemoveImage(img);
                        anyRemoved = true;
                    }
                    catch (InvalidOperationException ex) { Logger.LogDebug(ex, "Could not remove existing {Type} for {PersonName}", typeName, record.PersonName); }
                    catch (IOException ex) { Logger.LogDebug(ex, "Could not remove existing {Type} file for {PersonName}", typeName, record.PersonName); }
                }
            }
        }

        if (anyRemoved)
        {
            try
            {
                await localPerson.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Persisting pre-clear of images for {PersonName} threw; SaveImage may still see stale rows", record.PersonName);
            }
        }

        var appliedAny = false;
        var downloadFailures = 0;
        var saveFailures = 0;

        foreach (var (imageType, imageIndex) in work)
        {
            var (stream, contentType) = await imageClient.DownloadItemImageAsync(
                sourcePersonId,
                imageType,
                imageIndex,
                cancellationToken).ConfigureAwait(false);

            if (stream == null)
            {
                downloadFailures++;
                Logger.LogWarning(
                    "Image apply for {PersonName}: download of {Type}/{Index} from source id {Id} returned null (404/timeout/network — see prior LogDebug for details). Source manifest claims this image exists; source server may have stale ImageTags pointing at deleted image data.",
                    record.PersonName, imageType, imageIndex, sourcePersonId);
                continue;
            }

            var tempPath = Path.GetTempFileName();
            long tempBytes = 0;
            try
            {
                await using (stream.ConfigureAwait(false))
                {
                    using var fileStream = File.Create(tempPath);
                    await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                }

                try { tempBytes = new FileInfo(tempPath).Length; } catch (IOException) { }

                if (tempBytes == 0)
                {
                    // 200 OK with empty body — source served the request but
                    // had no actual image data. SaveImage will silently no-op
                    // on a zero-byte file, which produces a "verify says local
                    // is empty" failure with no other clue. Skip the save and
                    // log loudly so the cause is visible.
                    Logger.LogWarning(
                        "Image apply for {PersonName}: download of {Type}/{Index} from source id {Id} returned 0 bytes (Content-Type: {Ct}). Source server delivered the response but had no image data — likely stale ImageTags pointing at deleted file.",
                        record.PersonName, imageType, imageIndex, sourcePersonId, contentType ?? "(none)");
                    downloadFailures++;
                    continue;
                }

                if (!string.IsNullOrEmpty(contentType)
                    && !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    // Non-image Content-Type means we got an error page (HTML
                    // 404, JSON error, etc.) wrapped in a 200 response. Don't
                    // save garbage as a Primary image.
                    Logger.LogWarning(
                        "Image apply for {PersonName}: download of {Type}/{Index} from source id {Id} returned non-image Content-Type '{Ct}' ({Bytes} bytes). Refusing to save.",
                        record.PersonName, imageType, imageIndex, sourcePersonId, contentType, tempBytes);
                    downloadFailures++;
                    continue;
                }

                if (Enum.TryParse<ImageType>(imageType, true, out var parsedImageType))
                {
                    int beforeCount = 0;
                    try { beforeCount = localPerson.GetImages(parsedImageType).Count(); }
                    catch (InvalidOperationException) { }
                    catch (IOException) { }

                    using var fileStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    try
                    {
                        await _providerManager.SaveImage(
                            localPerson,
                            fileStream,
                            contentType ?? "image/jpeg",
                            parsedImageType,
                            imageIndex,
                            cancellationToken).ConfigureAwait(false);

                        appliedAny = true;

                        int afterCount = 0;
                        try { afterCount = localPerson.GetImages(parsedImageType).Count(); }
                        catch (InvalidOperationException) { }
                        catch (IOException) { }

                        // If SaveImage returned without throwing but the in-
                        // memory ImageInfos list didn't gain an entry, we
                        // silently no-op'd. Surface that explicitly so the
                        // verify failure is traceable.
                        if (afterCount <= beforeCount)
                        {
                            Logger.LogWarning(
                                "Image apply for {PersonName}: SaveImage returned for {Type}/{Index} ({Bytes} bytes) but local count did not increase ({Before} → {After}). SaveImage silently no-op'd — likely an unsupported image type for Person items, a path issue, or a Jellyfin internal rejection.",
                                record.PersonName, imageType, imageIndex, tempBytes, beforeCount, afterCount);
                        }
                        else
                        {
                            Logger.LogInformation(
                                "Image apply for {PersonName}: SaveImage applied {Type}/{Index} ({Bytes} bytes); local count {Before} → {After}",
                                record.PersonName, imageType, imageIndex, tempBytes, beforeCount, afterCount);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        saveFailures++;
                        Logger.LogWarning(ex, "Image apply for {PersonName}: SaveImage threw for {Type}/{Index} ({Bytes} bytes)", record.PersonName, imageType, imageIndex, tempBytes);
                    }
                }
                else
                {
                    Logger.LogWarning("Image apply for {PersonName}: unknown image type '{Type}' returned by source", record.PersonName, imageType);
                }
            }
            finally
            {
                try { File.Delete(tempPath); } catch (IOException) { }
            }
        }

        if (!appliedAny)
        {
            Logger.LogWarning(
                "Image apply for {PersonName} produced no successful saves: {Total} attempted, {DownloadFails} download failures, {SaveFails} save failures, fellBack={FellBack}",
                record.PersonName, work.Count, downloadFailures, saveFailures, fellBack);
            return false;
        }

        // Persist the image mutations to the repository. SaveImage updates
        // localPerson._imageInfos in memory, but the DB write does NOT
        // happen reliably for Person items — the verify path's
        // GetItemById call reloads from the DB and reports 0 images
        // unless we force a persist here. The metadata sync's
        // ApplyImagesAsync has had this call all along; people sync was
        // missing it, which is why every record that genuinely needed an
        // image sync was failing verification with "local manifest empty,
        // source non-empty" while the in-memory localPerson count went
        // 0 → 1 successfully.
        try
        {
            await localPerson.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Image apply for {PersonName}: UpdateToRepositoryAsync(ImageUpdate) threw after SaveImage; image mutations may not have persisted", record.PersonName);
        }

        return appliedAny;
    }

    // ===================================================================
    // Verification helpers
    // ===================================================================

    private (bool Succeeded, string? FailureReason) VerifyMetadataApplied(BaseItem freshPerson, PeopleSyncItem record)
    {
        // Rebuild the local blob via the canonical builder, then JSON-equals
        // it against source. The service's Build* helpers normalize ordering
        // and apply the same Kiota-unwrap so the comparison is apples-to-apples.
        var freshBlob = PeopleSyncTableService.BuildLocalMetadata(freshPerson);
        record.Metadata.Local = freshBlob;
        if (JsonComparisonUtility.JsonEquals(freshBlob, record.Metadata.Source))
        {
            return (true, null);
        }

        var differingFields = JsonComparisonUtility.GetDifferingFields(record.Metadata.Source, freshBlob);
        var fieldList = differingFields.Count == 0 ? "(none)" : string.Join(",", differingFields);
        var detail = JsonComparisonUtility.DescribeDifferingFields(record.Metadata.Source, freshBlob);
        return (false, $"verification found {differingFields.Count} divergent field(s) [{fieldList}]; {detail}");
    }

    private (bool Succeeded, string? FailureReason) VerifyImagesApplied(BaseItem freshPerson, PeopleSyncItem record)
    {
        // Diagnostic: log what GetImages actually returns at verify time so
        // we can see the gap between "SaveImage returned without throwing"
        // and "freshPerson reports zero images of that type". If these
        // numbers disagree with the post-SaveImage count from the apply
        // path, we have a stale-cache or different-instance problem.
        try
        {
            var primaryCount = freshPerson.GetImages(MediaBrowser.Model.Entities.ImageType.Primary).Count();
            Logger.LogInformation(
                "Verify Images for {PersonName}: freshPerson (id={Id}) reports {Count} Primary image(s)",
                record.PersonName, freshPerson.Id, primaryCount);
        }
        catch (InvalidOperationException) { }
        catch (IOException) { }

        var (_, freshLocal) = _peopleService.PopulateImageData(null, freshPerson);
        record.Images.Local = freshLocal;

        if (record.Images.Comparator is Models.Common.Comparators.ImageManifestComparator imagesComparator)
        {
            var diff = imagesComparator.DescribeDifference(record.Images.Source, freshLocal);
            if (diff == null)
            {
                return (true, null);
            }

            return (false, $"image manifest after apply does not match source manifest: {diff}");
        }

        if (record.Images.Comparator.Equals(record.Images.Source, freshLocal))
        {
            return (true, null);
        }

        return (false, "image manifest after apply does not match source manifest");
    }

    // ===================================================================
    // Local helpers
    // ===================================================================

}
