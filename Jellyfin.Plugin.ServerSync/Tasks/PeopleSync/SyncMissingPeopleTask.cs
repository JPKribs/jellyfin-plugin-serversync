using System;
using System.Collections.Concurrent;
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

    /// <summary>The categories each row verified, kept until its outcome is logged.</summary>
    private readonly ConcurrentDictionary<PeopleSyncItem, string> _verified = new(ReferenceEqualityComparer.Instance);

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
                Logger.LogDebug(ex, "Metadata apply threw for {PersonName}", FormatUtilities.SanitizeForLog(record.PersonName));
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
                Logger.LogDebug(ex, "UpdateToRepositoryAsync threw for {PersonName}", FormatUtilities.SanitizeForLog(record.PersonName));
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
                    Logger.LogDebug(ex, "Images apply threw for {PersonName}", FormatUtilities.SanitizeForLog(record.PersonName));
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
                Logger.LogDebug("Apply Metadata verified for person {PersonName}", FormatUtilities.SanitizeForLog(record.PersonName));
            }
            else
            {
                failures.Add($"Metadata: {reason}");
                Logger.LogDebug("Apply Metadata failed verification for person {PersonName}: {Reason}", FormatUtilities.SanitizeForLog(record.PersonName), FormatUtilities.SanitizeForLog(reason, 300));
            }
        }

        if (syncImages && record.HasImagesChanges)
        {
            var (ok, reason) = await VerifyImagesAppliedAsync(freshPerson, record, cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                record.Images.MarkSynced();
                synced.Add("Images");
                Logger.LogDebug("Apply Images verified for person {PersonName}", FormatUtilities.SanitizeForLog(record.PersonName));
            }
            else
            {
                failures.Add($"Images: {reason}");
                Logger.LogDebug("Apply Images failed verification for person {PersonName}: {Reason}", FormatUtilities.SanitizeForLog(record.PersonName), FormatUtilities.SanitizeForLog(reason, 300));
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", failures));
        }

        if (synced.Count > 0)
        {
            _verified[record] = string.Join(", ", synced);
        }
    }

    /// <summary>
    /// Writes one line for a row's outcome. A person who synced names the categories updated, one with nothing to update is skipped, and one that errored gives the reason.
    /// </summary>
    /// <inheritdoc />
    protected override void LogOutcome(PeopleSyncItem record, Exception? error)
    {
        ArgumentNullException.ThrowIfNull(record);
        _verified.TryRemove(record, out var categories);
        var name = FormatUtilities.SanitizeForLog(record.PersonName);
        if (error is not null)
        {
            Logger.LogError(error, "{Task}: could not update {PersonName}", Name, name);
        }
        else if (string.IsNullOrEmpty(categories))
        {
            Logger.LogDebug("{Task}: skipped {PersonName}, nothing to update", Name, name);
        }
        else
        {
            Logger.LogDebug("{Task}: updated {Categories} for {PersonName}", Name, categories, name);
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
                        Logger.LogDebug(
                            "Image apply for {PersonName}: /Items/{Id}/Images returned no entries. Falling back to cached ImageTags manifest with {Count} type(s)",
                            FormatUtilities.SanitizeForLog(record.PersonName), sourcePersonId, work.Count);
                    }
                }
            }
            catch (JsonException ex)
            {
                Logger.LogDebug(ex, "Could not parse cached image manifest for {PersonName}. Falling back to no-op", FormatUtilities.SanitizeForLog(record.PersonName));
            }
        }

        if (work.Count == 0)
        {
            return;
        }

        var ordered = ImageUtilities.AssignSequentialSlots(work)
            .Select(slot => new ImageToApply(slot.ImageType, slot.SourceIndex, slot.TargetIndex))
            .ToList();
        var personContext = record.PersonName ?? record.SourcePersonId ?? localPerson.Id.ToString();

        var result = await ImageUtilities.ReplaceAsync(
            localPerson,
            ordered,
            (image, ct) => imageClient.DownloadItemImageAsync(sourcePersonId, image.ImageType, image.SourceIndex, ct),
            _providerManager,
            Path.Combine(ConfigManager.GetTempDownloadPath(), "person-images"),
            Logger,
            personContext,
            cancellationToken).ConfigureAwait(false);

        // What did not land is left to the verify step, which fails the row when the images still differ.
        if (result.Saved == 0)
        {
            Logger.LogDebug(
                "Image apply for {PersonName} produced no successful saves: {Total} attempted, {DownloadFails} download failures, {SaveFails} save failures, fellBack={FellBack}",
                FormatUtilities.SanitizeForLog(record.PersonName), result.Attempted, result.DownloadFailures, result.SaveFailures, fellBack);
            return;
        }

        // Without this save the image change stays in memory only.
        try
        {
            await localPerson.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Logger.LogDebug(ex, "Image apply for {PersonName}: UpdateToRepositoryAsync(ImageUpdate) threw after SaveImage. Image mutations may not have persisted", FormatUtilities.SanitizeForLog(record.PersonName));
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
            Logger.LogDebug(
                "Verify Images for {PersonName}: freshPerson (id={Id}) reports {Count} Primary image(s)",
                FormatUtilities.SanitizeForLog(record.PersonName), freshPerson.Id, primaryCount);
        }
        catch (InvalidOperationException) { }
        catch (IOException) { }

        record.Images.Local = ImageUtilities.BuildLocalManifest(freshPerson, PeopleSyncMergeService.ImageTypes, record.Images.Local);

        return await ImageUtilities.VerifyAsync(
            record.Images,
            Guid.TryParse(record.SourcePersonId, out var sourcePersonGuid) ? sourcePersonGuid : null,
            SourceFor(record)?.Client,
            Logger,
            record.PersonName ?? record.SourcePersonId ?? freshPerson.Id.ToString(),
            null,
            cancellationToken).ConfigureAwait(false);
    }
}
