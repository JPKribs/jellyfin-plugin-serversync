using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.MetadataSync;

/// <summary>
/// Applies queued <see cref="MetadataSyncItem"/> rows, parents first, writing each category that is on to the local item.
/// A category that reads back correctly is marked synced on its own, and one that fails makes the row <see cref="SyncStatus.Errored"/> with a reason naming the category.
/// </summary>
public class SyncMissingMetadataTask : SyncQueueTaskBase<MetadataSyncItem, (string SourceLibraryId, string SourceItemId)>
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly MetadataSyncTableService _metadataService;
    private readonly AppliedVersionRecorder _applied;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncMissingMetadataTask"/> class.
    /// </summary>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="libraryManager">Reads and changes Jellyfin's library.</param>
    /// <param name="providerManager">Refreshes an item's metadata and images.</param>
    /// <param name="clientFactory">Builds the client for each source.</param>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="metadataService">Builds and compares metadata sync rows.</param>
    /// <param name="manager">Reads and writes the sync table.</param>
    /// <param name="applied">Records the stamp each applied value carries.</param>
    public SyncMissingMetadataTask(
        ILogger<SyncMissingMetadataTask> logger,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        ISourceServerClientFactory clientFactory,
        IPluginConfigurationManager configManager,
        MetadataSyncTableService metadataService,
        MetadataSyncTableManager manager,
        AppliedVersionRecorder applied)
        : base(logger, manager, clientFactory, configManager)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _metadataService = metadataService;
        _applied = applied;
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

    /// <summary>
    /// Applies four rows at once, since image downloads dominate the time each item takes.
    /// Jellyfin's repository locks its own writes, so concurrent applies still store one at a time.
    /// </summary>
    /// <inheritdoc />
    protected override int MaxDegreeOfParallelism => 4;

    /// <inheritdoc />
    protected override bool IsEnabled()
    {
        var config = ConfigManager.Configuration;
        if (!config.EnableMetadataSync) return false;
        if (config.GetPullServers().Count == 0) return false;
        if (config.GetEnabledLibraryMappings().Count == 0) return false;
        return config.MetadataSyncMetadata || config.MetadataSyncImages
            || config.MetadataSyncPeople || config.MetadataSyncStudios;
    }

    /// <summary>
    /// Returns the queued rows with folder types first, so a parent's metadata is in place before its children.
    /// </summary>
    /// <inheritdoc />
    protected override IList<MetadataSyncItem> GetItemsToApply()
    {
        var items = Manager.GetByStatusStrict(SyncStatus.Queued).ToList();
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

    /// <summary>
    /// Groups the rows by tier, since a season must not apply while its series is still in flight.
    /// </summary>
    /// <inheritdoc />
    protected override IEnumerable<IList<MetadataSyncItem>> GetApplyGroups(IList<MetadataSyncItem> items)
    {
        return items
            .GroupBy(i => GetItemTypeOrder(i.ItemType))
            .OrderBy(g => g.Key)
            .Select(g => (IList<MetadataSyncItem>)g.ToList());
    }

    /// <summary>
    /// Only the item itself is held for the whole apply. Writing its cast touches Person items too, so
    /// ApplyAsync holds every people hint back as well, but only from the cast write to the save, never
    /// while images download.
    /// </summary>
    /// <inheritdoc />
    protected override IDisposable? EnterApplyGuard(MetadataSyncItem record)
        => _applied.Enter(Models.Queue.HintKind.Metadata, record?.LocalItemId ?? string.Empty);

    /// <inheritdoc />
    protected override Task AfterApplySucceededAsync(MetadataSyncItem record, CancellationToken cancellationToken)
        => _applied.RecordAsync(Models.Queue.HintKind.Metadata, record?.LocalItemId ?? string.Empty, record?.SourceItemId ?? string.Empty, SourceFor(record!), cancellationToken);

    /// <inheritdoc />
    protected override async Task ApplyAsync(MetadataSyncItem record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var localItem = ResolveLocalItem(record);
        var config = ConfigManager.Configuration;
        var failures = new List<string>();

        var metadataChanged = false;
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
                Logger.LogError(ex, "Metadata apply threw for {ItemName}", FormatUtilities.SanitizeForLog(record.ItemName));
                failures.Add($"Metadata: apply threw, {ex.Message}");
            }
        }

        if (config.MetadataSyncImages && record.HasImagesChanges)
        {
            anyApplyAttempted = true;
            try
            {
                await ApplyImagesAsync(localItem, record, RequireSource(record).Client, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Logger.LogError(ex, "Images apply threw for {ItemName}", FormatUtilities.SanitizeForLog(record.ItemName));
                failures.Add($"Images: apply threw, {ex.Message}");
            }
        }

        // Held from the cast write through the save, since both touch Person items.
        using var peopleScope = config.MetadataSyncPeople && record.HasPeopleChanges && localItem.SupportsPeople
            ? _applied.EnterAll(Models.Queue.HintKind.People)
            : null;

        if (config.MetadataSyncPeople && record.HasPeopleChanges)
        {
            anyApplyAttempted = true;
            if (!localItem.SupportsPeople)
            {
                failures.Add($"People: item type {localItem.GetType().Name} does not accept manual people writes");
                Logger.LogWarning(
                    "Cannot sync people for {ItemName} ({ItemType}): SupportsPeople is false. Local id {LocalItemId}",
                    FormatUtilities.SanitizeForLog(record.ItemName),
                    localItem.GetType().Name,
                    localItem.Id);
            }
            else
            {
                try
                {
                    peopleChanged = await ApplyPeopleAsync(localItem, record, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    Logger.LogError(ex, "People apply threw for {ItemName}", FormatUtilities.SanitizeForLog(record.ItemName));
                    failures.Add($"People: apply threw, {ex.Message}");
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
                Logger.LogError(ex, "Studios apply threw for {ItemName}", FormatUtilities.SanitizeForLog(record.ItemName));
                failures.Add($"Studios: apply threw, {ex.Message}");
            }
        }

        // Images saved themselves already, so they take no part in this save.
        if (anyApplyAttempted && (metadataChanged || peopleChanged || studiosChanged))
        {
            try
            {
                await localItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Logger.LogError(ex, "UpdateToRepositoryAsync(MetadataEdit) threw for {ItemName}", FormatUtilities.SanitizeForLog(record.ItemName));
                failures.Add($"Persist: UpdateToRepositoryAsync threw, {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", failures));
        }
    }

    /// <summary>
    /// Reads the local item back and confirms each applied category landed, since Jellyfin's providers can rewrite a value between apply and save.
    /// A category that passes is marked synced on its own, so its progress is kept when another fails and the row errors.
    /// </summary>
    /// <inheritdoc />
    protected override async Task VerifyAfterApplyAsync(MetadataSyncItem record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var config = ConfigManager.Configuration;

        // Nothing was applied, so there is nothing to read back.
        var anyCandidate = (config.MetadataSyncMetadata && record.HasMetadataChanges)
            || (config.MetadataSyncImages && record.HasImagesChanges)
            || (config.MetadataSyncPeople && record.HasPeopleChanges)
            || (config.MetadataSyncStudios && record.HasStudiosChanges);
        if (!anyCandidate)
        {
            return;
        }

        if (!Guid.TryParse(record.LocalItemId!, out var localItemId))
        {
            throw new InvalidOperationException("Invalid item ID");
        }

        var freshLocal = _libraryManager.GetItemById(localItemId)
            ?? throw new InvalidOperationException("Verify: local item disappeared after apply");

        var failures = new List<string>();
        var synced = new List<string>();

        if (config.MetadataSyncMetadata && record.HasMetadataChanges)
        {
            var (ok, reason) = VerifyMetadataApplied(freshLocal, record, config.MetadataSyncGenres, config.MetadataSyncTags);
            if (ok)
            {
                record.Metadata.MarkSynced();
                synced.Add("Metadata");
                Logger.LogInformation("Apply Metadata verified for {ItemName}", FormatUtilities.SanitizeForLog(record.ItemName));
            }
            else
            {
                failures.Add($"Metadata: {reason}");
                Logger.LogWarning("Apply Metadata failed verification for {ItemName}: {Reason}", FormatUtilities.SanitizeForLog(record.ItemName), FormatUtilities.SanitizeForLog(reason, 300));
            }
        }

        if (config.MetadataSyncImages && record.HasImagesChanges)
        {
            var (ok, reason) = await VerifyImagesAppliedAsync(record, cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                record.Images.MarkSynced();
                synced.Add("Images");
                Logger.LogInformation("Apply Images verified for {ItemName}", FormatUtilities.SanitizeForLog(record.ItemName));
            }
            else
            {
                failures.Add($"Images: {reason}");
                Logger.LogWarning("Apply Images failed verification for {ItemName}: {Reason}", FormatUtilities.SanitizeForLog(record.ItemName), FormatUtilities.SanitizeForLog(reason, 300));
            }
        }

        if (config.MetadataSyncPeople && record.HasPeopleChanges && freshLocal.SupportsPeople)
        {
            var (ok, reason) = VerifyPeopleApplied(freshLocal, record);
            if (ok)
            {
                record.People.MarkSynced();
                synced.Add("People");
                Logger.LogInformation("Apply People verified for {ItemName}", FormatUtilities.SanitizeForLog(record.ItemName));
            }
            else
            {
                failures.Add($"People: {reason}");
                Logger.LogWarning("Apply People failed verification for {ItemName}: {Reason}", FormatUtilities.SanitizeForLog(record.ItemName), FormatUtilities.SanitizeForLog(reason, 300));
            }
        }

        if (config.MetadataSyncStudios && record.HasStudiosChanges)
        {
            var (ok, reason) = VerifyStudiosApplied(freshLocal, record);
            if (ok)
            {
                record.Studios.MarkSynced();
                synced.Add("Studios");
                Logger.LogInformation("Apply Studios verified for {ItemName}", FormatUtilities.SanitizeForLog(record.ItemName));
            }
            else
            {
                failures.Add($"Studios: {reason}");
                Logger.LogWarning("Apply Studios failed verification for {ItemName}: {Reason}", FormatUtilities.SanitizeForLog(record.ItemName), FormatUtilities.SanitizeForLog(reason, 300));
            }
        }

        if (failures.Count > 0)
        {
            // Categories that passed keep their synced state, so the next run skips them.
            throw new InvalidOperationException(string.Join("; ", failures));
        }

        if (synced.Count > 0)
        {
            Logger.LogDebug("Synced {Categories} for {ItemName}", string.Join(", ", synced), FormatUtilities.SanitizeForLog(record.ItemName));
        }
    }

    /// <summary>Finds the row's local item, or throws with the reason the row carries.</summary>
    private BaseItem ResolveLocalItem(MetadataSyncItem record)
    {
        if (string.IsNullOrEmpty(record.LocalItemId))
        {
            throw new InvalidOperationException("Local item not found");
        }

        if (!Guid.TryParse(record.LocalItemId, out var localItemId))
        {
            throw new InvalidOperationException("Invalid item ID");
        }

        return _libraryManager.GetItemById(localItemId)
            ?? throw new InvalidOperationException("Local item not found in library");
    }

    /// <summary>
    /// Does nothing, since <see cref="VerifyAfterApplyAsync"/> marks each category that passed.
    /// Marking the whole row would mark categories that were not applied.
    /// </summary>
    /// <inheritdoc />
    protected override void OnApplySucceeded(MetadataSyncItem record)
    {
        _ = record;
    }

    /// <inheritdoc />
    protected override void RecordRunCompleted(PluginConfiguration config, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.LastMetadataSyncTime = utcNow;
    }

    /// <summary>
    /// Writes the metadata fields to the local item, nulls and empty lists included, so a value cleared on the source is cleared here.
    /// It does not save the item, since the caller saves every category at once.
    /// </summary>
    /// <returns><c>true</c> when anything changed.</returns>
    private static bool ApplyMetadata(
        BaseItem localItem,
        MetadataSyncItem item,
        bool syncGenres,
        bool syncTags)
        => MetadataFieldWriter.Apply(localItem, item.Metadata.Source, syncGenres, syncTags);

    /// <summary>
    /// Replaces the local item's images with the source's, downloading each type to temp files first, so a failed download leaves the existing images intact.
    /// It saves the item itself, because <see cref="IProviderManager.SaveImage"/> needs the item in the repository.
    /// </summary>
    private async Task ApplyImagesAsync(
        BaseItem localItem,
        MetadataSyncItem item,
        SourceServerClient sourceClient,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.Images.Source))
        {
            return;
        }

        var sourceImagesByType = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(item.Images.Source);
        if (sourceImagesByType == null || sourceImagesByType.Count == 0)
        {
            return;
        }

        if (!Guid.TryParse(item.SourceItemId, out var sourceItemGuid))
        {
            throw new InvalidOperationException($"Invalid source item ID for image sync: {item.SourceItemId}");
        }

        var anyChanged = false;
        // Collected so the row errors when only some image types landed.
        var perTypeErrors = new List<string>();

        foreach (var kvp in sourceImagesByType)
        {
            var imageTypeName = kvp.Key;
            var sourceImages = kvp.Value;

            if (!Enum.TryParse<ImageType>(imageTypeName, out var imageType))
            {
                Logger.LogWarning("Unknown image type: {ImageType}", FormatUtilities.SanitizeForLog(imageTypeName));
                perTypeErrors.Add($"{imageTypeName}: unknown image type");
                continue;
            }

            // One failed image fails its type, and the other types still go on.
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
                                FormatUtilities.SanitizeForLog(imageTypeName), i, localItem.Name);
                            typeErrors.Add($"download index {i}: returned null");
                            continue;
                        }

                        var tempDir = Path.Combine(ConfigManager.GetTempDownloadPath(), "metadata-images");
                        Directory.CreateDirectory(tempDir);
                        var tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));
                        try
                        {
                            using (imageStream)
                            {
                                // What is saved is named by its type, so only an image is taken.
                                if (contentType is not null && !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                                {
                                    throw new InvalidOperationException($"the source answered {contentType}, which is not an image");
                                }

                                using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920);
                                await StallWatchdogStream.CopyAsync(new CappedReadStream(imageStream, StreamCaps.MaxSyncedImageBytes), fileStream, StallWatchdogStream.DefaultIdleTimeout, cancellationToken).ConfigureAwait(false);
                            }

                            tempFiles.Add((i, tempPath, contentType ?? "image/jpeg"));
                        }
                        catch
                        {
                            try { File.Delete(tempPath); } catch (IOException) { }
                            throw;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        Logger.LogWarning(ex, "Error downloading {ImageType}/{Index} image for {ItemName}",
                            FormatUtilities.SanitizeForLog(imageTypeName), i, localItem.Name);
                        typeErrors.Add($"download index {i}: {ex.Message}");
                    }
                }

                // A failed download keeps the existing images, so no type is half replaced.
                if (typeErrors.Count > 0)
                {
                    Logger.LogWarning(
                        "Not all {ImageType} downloads succeeded for {ItemName} ({Downloaded}/{Total}), keeping existing local images",
                        FormatUtilities.SanitizeForLog(imageTypeName), localItem.Name, tempFiles.Count, sourceImages.Count);
                    perTypeErrors.Add($"{imageTypeName}: {string.Join("; ", typeErrors)} (kept existing local images)");
                    continue;
                }

                if (tempFiles.Count == 0)
                {
                    // Every download was empty, so nothing failed and nothing changes.
                    continue;
                }

                // The type is wiped first, or leftover extra images would be adopted again.
                var cleared = await LocalImageUtilities.ClearImagesAsync(
                    localItem, imageType, Logger, localItem.Name ?? item.ItemName ?? localItem.Id.ToString(), cancellationToken).ConfigureAwait(false);
                if (cleared > 0)
                {
                    anyChanged = true;
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
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        Logger.LogWarning(ex, "Error saving {ImageType}/{Index} image for {ItemName}",
                            FormatUtilities.SanitizeForLog(imageTypeName), index, localItem.Name);
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

        // Without the throw, a half applied image set would be marked synced.
        if (perTypeErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Image apply incomplete for {item.ItemName}: {string.Join(" | ", perTypeErrors)}");
        }
    }

    /// <summary>
    /// Applies the cast to the local item without saving it, since the caller saves every category at once.
    /// </summary>
    private async Task<bool> ApplyPeopleAsync(
        BaseItem localItem,
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

        var people = new List<PersonInfo>();
        foreach (var person in peopleList)
        {
            var personInfo = new PersonInfo();

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
                // Jellyfin orders credits by SortOrder, so the source's billing order is pinned.
                personInfo.SortOrder = people.Count;
                people.Add(personInfo);
            }
        }

        await _libraryManager.UpdatePeopleAsync(localItem, people, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Applies the studios to the local item without saving it, since the caller saves every category at once.
    /// </summary>
    private bool ApplyStudios(
        BaseItem localItem,
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

    /// <summary>Rebuilds the local snapshot and compares it with the source's metadata.</summary>
    private (bool Succeeded, string? FailureReason) VerifyMetadataApplied(
        BaseItem freshLocal,
        MetadataSyncItem record,
        bool syncGenres,
        bool syncTags)
    {
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
        return (false, $"verification found {JPKribs.Jellyfin.Base.ActivityText.Count(differingFields.Count, "divergent field")} [{fieldList}] (item type {freshLocal.GetType().Name}). {detail}");
    }

    /// <summary>Rebuilds the local image manifest and compares it with the source's, with real sizes.</summary>
    private async Task<(bool Succeeded, string? FailureReason)> VerifyImagesAppliedAsync(
        MetadataSyncItem record,
        CancellationToken cancellationToken)
    {
        _metadataService.RefreshLocalSnapshot(record, syncMetadata: false, syncImages: true, syncPeople: false, syncStudios: false, syncGenres: false, syncTags: false);

        // Scan manifests carry no sizes, so the source side gets real sizes here.
        string? enrichedSource = record.Images.Source;
        var imageSource = SourceFor(record);
        if (imageSource != null && Guid.TryParse(record.SourceItemId, out var sourceItemGuid))
        {
            try
            {
                enrichedSource = await ImageManifestEnricher.EnrichAsync(
                    record.Images.Source,
                    sourceItemGuid,
                    imageSource.Client,
                    Logger,
                    record.ItemName ?? record.SourceItemId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Logger.LogWarning(ex, "Source image enrichment for verify failed for {ItemName}. Falling back to tag-only comparison", FormatUtilities.SanitizeForLog(record.ItemName));
            }
        }

        // Profile images cannot be saved on these items, so they are left out.
        var sanitizedSource = StripUnsyncableImageTypes(enrichedSource);

        if (record.Images.Comparator is Models.Common.Comparators.ImageManifestComparator imagesComparator)
        {
            var diff = imagesComparator.DescribeDifference(sanitizedSource, record.Images.Local);
            if (diff == null)
            {
                return (true, null);
            }

            return (false, $"image manifest after apply does not match source manifest: {diff}");
        }

        if (record.Images.Comparator.Equals(sanitizedSource, record.Images.Local))
        {
            return (true, null);
        }

        return (false, "image manifest after apply does not match source manifest (count or per-type tag/size mismatch)");
    }

    /// <summary>Rebuilds the local cast and compares it with the source's.</summary>
    private (bool Succeeded, string? FailureReason) VerifyPeopleApplied(
        BaseItem freshLocal,
        MetadataSyncItem record)
    {
        // The scan's own comparator, or a verified row would be queued again.
        _metadataService.RefreshLocalSnapshot(record, syncMetadata: false, syncImages: false, syncPeople: true, syncStudios: false, syncGenres: false, syncTags: false);

        var diff = record.People.Comparator is Models.Common.Comparators.PeopleListComparator comparator
            ? comparator.DescribeDifference(record.People.Source, record.People.Local)
            : (record.People.Comparator.Equals(record.People.Source, record.People.Local) ? null : "people differ");

        return diff == null
            ? (true, null)
            : (false, $"people after apply do not match source: {diff} (item type: {freshLocal.GetType().Name})");
    }

    /// <summary>Compares the local studios with the source's, ignoring case and order.</summary>
    private (bool Succeeded, string? FailureReason) VerifyStudiosApplied(
        BaseItem freshLocal,
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
    /// Returns the order item types sync in, parents first, where lower values sync first.
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

    /// <summary>
    /// Removes Profile, the image type that cannot be written to an item that is not a person.
    /// Jellyfin drops Profile writes on those items, so the entry would hold the row in Errored over a difference that can never sync.
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
}
