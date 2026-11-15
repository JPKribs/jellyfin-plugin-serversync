using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.PeopleSync;

/// <summary>
/// Applies queued People rows by writing metadata and images to the local person, then reads them back.
/// A category that did not land errors the row with a precise <see cref="SyncRecord.Reason"/>.
/// </summary>
public class SyncMissingPeopleTask : SyncQueueTaskBase<PeopleSyncItem, string>
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly AppliedVersionRecorder _applied;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncMissingPeopleTask"/> class.
    /// </summary>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="libraryManager">Reads and changes Jellyfin's library.</param>
    /// <param name="providerManager">Refreshes an item's metadata and images.</param>
    /// <param name="clientFactory">Builds the client for each source.</param>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="manager">Reads and writes the sync table.</param>
    /// <param name="applied">Records the stamp each applied value carries.</param>
    public SyncMissingPeopleTask(
        ILogger<SyncMissingPeopleTask> logger,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        ISourceServerClientFactory clientFactory,
        IPluginConfigurationManager configManager,
        PeopleSyncTableManager manager,
        AppliedVersionRecorder applied)
        : base(logger, manager, clientFactory, configManager)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _applied = applied;
    }

    /// <inheritdoc />
    protected override IDisposable? EnterApplyGuard(PeopleSyncItem record)
        => _applied.Enter(Models.Queue.HintKind.People, HintProtocol.PeopleKey(record?.PersonName ?? string.Empty));

    /// <inheritdoc />
    protected override Task AfterApplySucceededAsync(PeopleSyncItem record, CancellationToken cancellationToken)
        => _applied.RecordAsync(Models.Queue.HintKind.People, HintProtocol.PeopleKey(record?.PersonName ?? string.Empty), HintProtocol.PeopleKey(record?.PersonName ?? string.Empty), SourceFor(record!), cancellationToken);

    /// <inheritdoc />
    public override string Name => "Sync People";

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
        return config.EnablePeopleSync && config.GetPullServers().Count > 0;
    }

    /// <inheritdoc />
    protected override async Task ApplyAsync(PeopleSyncItem record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var (_, localPerson) = ResolveLocalPerson(record);
        var config = ConfigManager.Configuration;
        var syncImages = config.PeopleSyncImages;
        var failures = new List<string>();

        var metadataChanged = false;

        if (record.HasMetadataChanges)
        {
            try
            {
                metadataChanged = ApplyMetadata(localPerson, record);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "Metadata apply threw for {PersonName}", FormatUtilities.SanitizeForLog(record.PersonName));
                failures.Add($"Metadata: apply threw, {ex.Message}");
            }
        }

        // Saved before the images, so a failed image never strands the change.
        if (metadataChanged)
        {
            try
            {
                await localPerson.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Logger.LogError(ex, "UpdateToRepositoryAsync threw for {PersonName}", FormatUtilities.SanitizeForLog(record.PersonName));
                failures.Add($"Persist: UpdateToRepositoryAsync threw, {ex.Message}");
            }
        }

        if (syncImages && record.HasImagesChanges && !string.IsNullOrEmpty(record.SourcePersonId))
        {
            if (!Guid.TryParse(record.SourcePersonId, out var sourceGuid))
            {
                failures.Add($"Images: invalid source person ID {record.SourcePersonId}");
            }
            else if (SourceFor(record) is not { } imageSource)
            {
                failures.Add("Images: source server is not available this run");
            }
            else
            {
                try
                {
                    await ApplyPersonImagesAsync(localPerson, sourceGuid, record, imageSource.Client, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    Logger.LogError(ex, "Images apply threw for {PersonName}", FormatUtilities.SanitizeForLog(record.PersonName));
                    failures.Add($"Images: apply threw, {ex.Message}");
                }
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", failures));
        }
    }

    /// <summary>
    /// Reads the local person back and confirms each applied category landed.
    /// A category that passes is marked synced on its own, so its progress is kept when another fails.
    /// </summary>
    /// <inheritdoc />
    protected override async Task VerifyAfterApplyAsync(PeopleSyncItem record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var config = ConfigManager.Configuration;
        var syncImages = config.PeopleSyncImages;

        var anyCandidate = record.HasMetadataChanges
            || (syncImages && record.HasImagesChanges && !string.IsNullOrEmpty(record.SourcePersonId));
        if (!anyCandidate)
        {
            return;
        }

        var (localPersonId, _) = ResolveLocalPerson(record);

        var freshPerson = _libraryManager.GetItemById(localPersonId)
            ?? throw new InvalidOperationException("Verify: person entity disappeared after apply");

        var failures = new List<string>();
        var synced = new List<string>();

        if (record.HasMetadataChanges)
        {
            var (ok, reason) = VerifyMetadataApplied(freshPerson, record);
            if (ok)
            {
                record.Metadata.MarkSynced();
                synced.Add("Metadata");
                Logger.LogInformation("Apply Metadata verified for person {PersonName}", FormatUtilities.SanitizeForLog(record.PersonName));
            }
            else
            {
                failures.Add($"Metadata: {reason}");
                Logger.LogWarning("Apply Metadata failed verification for person {PersonName}: {Reason}", FormatUtilities.SanitizeForLog(record.PersonName), FormatUtilities.SanitizeForLog(reason, 300));
            }
        }

        if (syncImages && record.HasImagesChanges)
        {
            var (ok, reason) = await VerifyImagesAppliedAsync(freshPerson, record, cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                record.Images.MarkSynced();
                synced.Add("Images");
                Logger.LogInformation("Apply Images verified for person {PersonName}", FormatUtilities.SanitizeForLog(record.PersonName));
            }
            else
            {
                failures.Add($"Images: {reason}");
                Logger.LogWarning("Apply Images failed verification for person {PersonName}: {Reason}", FormatUtilities.SanitizeForLog(record.PersonName), FormatUtilities.SanitizeForLog(reason, 300));
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", failures));
        }

        if (synced.Count > 0)
        {
            Logger.LogDebug("Synced {Categories} for person {PersonName}", string.Join(", ", synced), FormatUtilities.SanitizeForLog(record.PersonName));
        }
    }

    /// <summary>
    /// Finds the local person, by the id the scan matched or else by name with a query.
    /// GetPerson is not used, because it creates a blank Person when none exists and one over the real Person when only the casing differs.
    /// </summary>
    private (Guid LocalPersonId, BaseItem LocalPerson) ResolveLocalPerson(PeopleSyncItem record)
    {
        if (Guid.TryParse(record.LocalPersonId, out var knownId)
            && _libraryManager.GetItemById(knownId) is Person known)
        {
            return (knownId, known);
        }

        if (string.IsNullOrEmpty(record.PersonName))
        {
            throw new InvalidOperationException("Local person not found: the row has no person name");
        }

        var found = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Person },
            Name = record.PersonName,
            Limit = 1
        });
        if (found is not { Count: > 0 } || !string.Equals(found[0].Name, record.PersonName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Local person not found: {record.PersonName}");
        }

        return (found[0].Id, found[0]);
    }

    /// <summary>
    /// Does nothing, since <see cref="VerifyAfterApplyAsync"/> marks each category that passed.
    /// Marking the whole row would mark categories that were not applied.
    /// </summary>
    /// <inheritdoc />
    protected override void OnApplySucceeded(PeopleSyncItem record)
    {
        _ = record;
    }

    /// <inheritdoc />
    protected override void RecordRunCompleted(PluginConfiguration config, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.LastPeopleSyncTime = utcNow;
    }

    /// <summary>
    /// Writes the metadata fields to the local person, nulls and empty lists included.
    /// It does not save the person, since the caller does.
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

        // Name is the match key and is never written.
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "OriginalTitle", v => { if (localPerson.OriginalTitle != v) { localPerson.OriginalTitle = v; return true; } return false; });
        // SortName is left out, since each server derives it from the name.
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "ForcedSortName", v => { if (localPerson.ForcedSortName != v) { localPerson.ForcedSortName = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "Overview", v => { if (localPerson.Overview != v) { localPerson.Overview = v; return true; } return false; });

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

        if (metadata.TryGetValue("ProductionYear", out var yearValue))
        {
            int? year = yearValue.ValueKind == JsonValueKind.Number ? yearValue.GetInt32() : null;
            if (localPerson.ProductionYear != year)
            {
                localPerson.ProductionYear = year;
                hasChanges = true;
            }
        }

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

        if (metadata.TryGetValue("ProviderIds", out var providerIdsValue))
        {
            var sourceIds = JsonFieldHelpers.ReadProviderIds(providerIdsValue);
            localPerson.ProviderIds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            hasChanges |= JsonFieldHelpers.ReconcileProviderIds(localPerson.ProviderIds, sourceIds);
        }

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

    /// <summary>Replaces the local person's images with the source's, downloading every image to a temp file first, so a failed download leaves the existing images intact.</summary>
    private async Task ApplyPersonImagesAsync(
        BaseItem localPerson,
        Guid sourcePersonId,
        PeopleSyncItem record,
        SourceServerClient imageClient,
        CancellationToken cancellationToken)
    {
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

        // The source sometimes lists no images for a person its tags describe.
        if (work.Count == 0 && !string.IsNullOrEmpty(record.Images.Source))
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
                            "Image apply for {PersonName}: /Items/{Id}/Images returned no entries. Falling back to cached ImageTags manifest with {Count} type(s)",
                            FormatUtilities.SanitizeForLog(record.PersonName), sourcePersonId, work.Count);
                    }
                }
            }
            catch (JsonException ex)
            {
                Logger.LogWarning(ex, "Could not parse cached image manifest for {PersonName}. Falling back to no-op", FormatUtilities.SanitizeForLog(record.PersonName));
            }
        }

        if (work.Count == 0)
        {
            return;
        }

        var ordered = LocalImageUtilities.AssignSequentialSlots(work);

        var personContext = record.PersonName ?? record.SourcePersonId ?? localPerson.Id.ToString();
        var appliedAny = false;
        var downloadFailures = 0;
        var saveFailures = 0;

        // Downloaded first, since the clear below is a hard delete.
        var tempDir = Path.Combine(ConfigManager.GetTempDownloadPath(), "person-images");
        Directory.CreateDirectory(tempDir);
        var downloaded = new List<(string ImageType, int? SourceIndex, int TargetIndex, string TempPath, string ContentType, long Bytes)>();
        var failedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var (imageType, imageIndex, targetIndex) in ordered)
            {
                var (stream, contentType) = await imageClient.DownloadItemImageAsync(
                    sourcePersonId,
                    imageType,
                    imageIndex,
                    cancellationToken).ConfigureAwait(false);

                if (stream == null)
                {
                    downloadFailures++;
                    failedTypes.Add(imageType);
                    Logger.LogWarning(
                        "Image apply for {PersonName}: download of {Type}/{Index} from source id {Id} returned null (404/timeout/network, see prior LogDebug for details). Source manifest claims this image exists. Source server may have stale ImageTags pointing at deleted image data.",
                        FormatUtilities.SanitizeForLog(record.PersonName), FormatUtilities.SanitizeForLog(imageType), imageIndex, sourcePersonId);
                    continue;
                }

                var tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));
                long tempBytes = 0;
                var buffered = false;
                try
                {
                    await using (stream.ConfigureAwait(false))
                    {
                        using var fileStream = File.Create(tempPath);
                        await StreamUtilities.CopyWithIdleTimeoutAsync(new CappedReadStream(stream, StreamCaps.MaxSyncedImageBytes), fileStream, StreamUtilities.DefaultIdleTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }

                    try { tempBytes = new FileInfo(tempPath).Length; } catch (IOException) { }

                    if (tempBytes == 0)
                    {
                        // An empty body would save nothing silently, so it counts as failed.
                        Logger.LogWarning(
                            "Image apply for {PersonName}: download of {Type}/{Index} from source id {Id} returned 0 bytes (Content-Type: {Ct}). Source server delivered the response but had no image data, likely stale ImageTags pointing at deleted file.",
                            FormatUtilities.SanitizeForLog(record.PersonName), FormatUtilities.SanitizeForLog(imageType), imageIndex, sourcePersonId, FormatUtilities.SanitizeForLog(contentType ?? "(none)"));
                        downloadFailures++;
                        failedTypes.Add(imageType);
                        continue;
                    }

                    if (!string.IsNullOrEmpty(contentType)
                        && !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        // A body that is not an image is an error page.
                        Logger.LogWarning(
                            "Image apply for {PersonName}: download of {Type}/{Index} from source id {Id} returned non-image Content-Type '{Ct}' ({Bytes} bytes). Refusing to save.",
                            FormatUtilities.SanitizeForLog(record.PersonName), FormatUtilities.SanitizeForLog(imageType), imageIndex, sourcePersonId, FormatUtilities.SanitizeForLog(contentType), tempBytes);
                        downloadFailures++;
                        failedTypes.Add(imageType);
                        continue;
                    }

                    if (!Enum.TryParse<ImageType>(imageType, true, out _))
                    {
                        Logger.LogWarning("Image apply for {PersonName}: unknown image type '{Type}' returned by source", FormatUtilities.SanitizeForLog(record.PersonName), FormatUtilities.SanitizeForLog(imageType));
                        failedTypes.Add(imageType);
                        continue;
                    }

                    downloaded.Add((imageType, imageIndex, targetIndex, tempPath, contentType ?? "image/jpeg", tempBytes));
                    buffered = true;
                }
                finally
                {
                    if (!buffered)
                    {
                        try { File.Delete(tempPath); } catch (IOException) { }
                    }
                }
            }

            // The type is wiped first, or a leftover image would be adopted again.
            foreach (var group in downloaded.GroupBy(d => d.ImageType, StringComparer.OrdinalIgnoreCase))
            {
                if (failedTypes.Contains(group.Key))
                {
                    // Partial set, so do not wipe what cannot be fully replaced.
                    Logger.LogWarning(
                        "Image apply for {PersonName}: not all {Type} downloads succeeded, keeping existing local images",
                        FormatUtilities.SanitizeForLog(record.PersonName), FormatUtilities.SanitizeForLog(group.Key));
                    continue;
                }

                if (!Enum.TryParse<ImageType>(group.Key, true, out var parsedImageType))
                {
                    continue;
                }

                await LocalImageUtilities.ClearImagesAsync(
                    localPerson, parsedImageType, Logger, personContext, cancellationToken).ConfigureAwait(false);

                foreach (var entry in group.OrderBy(d => d.TargetIndex))
                {
                    int beforeCount = 0;
                    try { beforeCount = localPerson.GetImages(parsedImageType).Count(); }
                    catch (InvalidOperationException) { }
                    catch (IOException) { }

                    using var fileStream = new FileStream(entry.TempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    try
                    {
                        await _providerManager.SaveImage(
                            localPerson,
                            fileStream,
                            entry.ContentType,
                            parsedImageType,
                            entry.TargetIndex,
                            cancellationToken).ConfigureAwait(false);

                        appliedAny = true;

                        int afterCount = 0;
                        try { afterCount = localPerson.GetImages(parsedImageType).Count(); }
                        catch (InvalidOperationException) { }
                        catch (IOException) { }

                        // A save that adds no image did nothing, which the log should show.
                        if (afterCount <= beforeCount)
                        {
                            Logger.LogWarning(
                                "Image apply for {PersonName}: SaveImage returned for {Type}/{Index} ({Bytes} bytes) but local count did not increase ({Before} → {After}). SaveImage silently no-op'd, likely an unsupported image type for Person items, a path issue, or a Jellyfin internal rejection.",
                                FormatUtilities.SanitizeForLog(record.PersonName), FormatUtilities.SanitizeForLog(entry.ImageType), entry.TargetIndex, entry.Bytes, beforeCount, afterCount);
                        }
                        else
                        {
                            Logger.LogInformation(
                                "Image apply for {PersonName}: SaveImage applied {Type}/{Index} ({Bytes} bytes). Local count {Before} → {After}",
                                FormatUtilities.SanitizeForLog(record.PersonName), FormatUtilities.SanitizeForLog(entry.ImageType), entry.TargetIndex, entry.Bytes, beforeCount, afterCount);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        saveFailures++;
                        Logger.LogWarning(ex, "Image apply for {PersonName}: SaveImage threw for {Type}/{Index} ({Bytes} bytes)", FormatUtilities.SanitizeForLog(record.PersonName), FormatUtilities.SanitizeForLog(entry.ImageType), entry.TargetIndex, entry.Bytes);
                    }
                }
            }
        }
        finally
        {
            foreach (var entry in downloaded)
            {
                try { File.Delete(entry.TempPath); } catch (IOException) { }
            }
        }

        if (!appliedAny)
        {
            Logger.LogWarning(
                "Image apply for {PersonName} produced no successful saves: {Total} attempted, {DownloadFails} download failures, {SaveFails} save failures, fellBack={FellBack}",
                FormatUtilities.SanitizeForLog(record.PersonName), work.Count, downloadFailures, saveFailures, fellBack);
            return;
        }

        // Without this save the image change stays in memory only.
        try
        {
            await localPerson.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning(ex, "Image apply for {PersonName}: UpdateToRepositoryAsync(ImageUpdate) threw after SaveImage. Image mutations may not have persisted", FormatUtilities.SanitizeForLog(record.PersonName));
        }
    }

    /// <summary>Rebuilds the local metadata and compares it with the source's.</summary>
    private (bool Succeeded, string? FailureReason) VerifyMetadataApplied(BaseItem freshPerson, PeopleSyncItem record)
    {
        var freshBlob = PeopleSyncMergeService.BuildLocalMetadata(freshPerson);
        record.Metadata.Local = freshBlob;
        if (JsonComparisonUtility.JsonEquals(freshBlob, record.Metadata.Source))
        {
            return (true, null);
        }

        var differingFields = JsonComparisonUtility.GetDifferingFields(record.Metadata.Source, freshBlob);
        var fieldList = differingFields.Count == 0 ? "(none)" : string.Join(",", differingFields);
        var detail = JsonComparisonUtility.DescribeDifferingFields(record.Metadata.Source, freshBlob);
        return (false, $"verification found {JPKribs.Jellyfin.Base.ActivityText.Count(differingFields.Count, "divergent field")} [{fieldList}]. {detail}");
    }

    /// <summary>Rebuilds the local image manifest and compares it with the source's, with real sizes.</summary>
    private async Task<(bool Succeeded, string? FailureReason)> VerifyImagesAppliedAsync(
        BaseItem freshPerson,
        PeopleSyncItem record,
        CancellationToken cancellationToken)
    {
        // Logged so a stale item cache shows in the count.
        try
        {
            var primaryCount = freshPerson.GetImages(ImageType.Primary).Count();
            Logger.LogInformation(
                "Verify Images for {PersonName}: freshPerson (id={Id}) reports {Count} Primary image(s)",
                FormatUtilities.SanitizeForLog(record.PersonName), freshPerson.Id, primaryCount);
        }
        catch (InvalidOperationException) { }
        catch (IOException) { }

        var (_, freshLocal) = PeopleSyncMergeService.PopulateImageData(null, freshPerson);
        record.Images.Local = freshLocal;

        // Scan manifests carry no sizes, so the source side gets real sizes here.
        string? enrichedSource = record.Images.Source;
        var verifySource = SourceFor(record);
        if (verifySource != null
            && !string.IsNullOrEmpty(record.SourcePersonId)
            && Guid.TryParse(record.SourcePersonId, out var sourcePersonGuid))
        {
            try
            {
                enrichedSource = await ImageManifestEnricher.EnrichAsync(
                    record.Images.Source,
                    sourcePersonGuid,
                    verifySource.Client,
                    Logger,
                    record.PersonName ?? record.SourcePersonId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Logger.LogWarning(ex, "Source image enrichment for verify failed for person {PersonName}. Falling back to tag-only comparison", FormatUtilities.SanitizeForLog(record.PersonName));
            }
        }

        if (record.Images.Comparator is Models.Common.Comparators.ImageManifestComparator imagesComparator)
        {
            var diff = imagesComparator.DescribeDifference(enrichedSource, freshLocal);
            if (diff == null)
            {
                return (true, null);
            }

            return (false, $"image manifest after apply does not match source manifest: {diff}");
        }

        if (record.Images.Comparator.Equals(enrichedSource, freshLocal))
        {
            return (true, null);
        }

        return (false, "image manifest after apply does not match source manifest");
    }
}
