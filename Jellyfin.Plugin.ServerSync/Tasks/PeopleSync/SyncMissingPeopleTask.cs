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
                    imagesChanged = await ApplyPersonImagesAsync(localPerson, sourceGuid, Client, cancellationToken).ConfigureAwait(false);
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
    /// No-op — per-category MarkSynced calls already happen in
    /// <see cref="ApplyAsync"/>. The default base behavior of
    /// <see cref="PeopleSyncItem.MarkSynced"/> would re-mark categories
    /// that this run intentionally skipped.
    /// </remarks>
    protected override void OnApplySucceeded(PeopleSyncItem record)
    {
        // Intentionally empty.
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
        hasChanges |= AssignString(metadata, "Name", v => { if (!string.IsNullOrEmpty(v) && localPerson.Name != v) { localPerson.Name = v; return true; } return false; });
        hasChanges |= AssignString(metadata, "OriginalTitle", v => { if (localPerson.OriginalTitle != v) { localPerson.OriginalTitle = v; return true; } return false; });
        // SortName intentionally not synced — Jellyfin derives it from
        // Name independently per server, so cross-server writes never
        // stick. ForcedSortName (user override) IS synced.
        hasChanges |= AssignString(metadata, "ForcedSortName", v => { if (localPerson.ForcedSortName != v) { localPerson.ForcedSortName = v; return true; } return false; });
        hasChanges |= AssignString(metadata, "Overview", v => { if (localPerson.Overview != v) { localPerson.Overview = v; return true; } return false; });

        // Dates — date-only compare.
        if (metadata.TryGetValue("PremiereDate", out var premiereValue))
        {
            var d = ParseNullableDate(premiereValue);
            if (!JsonComparisonUtility.DateOnlyEquals(localPerson.PremiereDate, d))
            {
                localPerson.PremiereDate = d;
                hasChanges = true;
            }
        }

        if (metadata.TryGetValue("EndDate", out var endValue))
        {
            var d = ParseNullableDate(endValue);
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
            var newLocations = ReadStringArray(locationsValue);
            var current = localPerson.ProductionLocations ?? Array.Empty<string>();
            if (!current.SequenceEqual(newLocations, StringComparer.Ordinal))
            {
                localPerson.ProductionLocations = newLocations;
                hasChanges = true;
            }
        }

        if (metadata.TryGetValue("Tags", out var tagsValue))
        {
            var newTags = ReadStringArray(tagsValue);
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
            var sourceIds = ReadProviderIds(providerIdsValue);
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
            var newLocked = ReadEnumArray<MetadataField>(lockedValue);
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
        SourceServerClient imageClient,
        CancellationToken cancellationToken)
    {
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

            var (stream, contentType) = await imageClient.DownloadItemImageAsync(
                sourcePersonId,
                imageType,
                imageInfo.ImageIndex,
                cancellationToken).ConfigureAwait(false);

            if (stream == null)
            {
                continue;
            }

            var tempPath = Path.GetTempFileName();
            try
            {
                await using (stream.ConfigureAwait(false))
                {
                    using var fileStream = File.Create(tempPath);
                    await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                }

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
                }
            }
            finally
            {
                try { File.Delete(tempPath); } catch (IOException) { }
            }
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

        var diff = JsonComparisonUtility.CountDifferences(record.Metadata.Source, freshBlob);
        return (false, $"verification found {diff} divergent field(s); source={TruncateForLog(record.Metadata.Source)}; local={TruncateForLog(freshBlob)}");
    }

    private (bool Succeeded, string? FailureReason) VerifyImagesApplied(BaseItem freshPerson, PeopleSyncItem record)
    {
        var (_, freshLocal) = _peopleService.PopulateImageData(null, freshPerson);
        record.Images.Local = freshLocal;

        if (record.Images.Comparator.Equals(record.Images.Source, freshLocal))
        {
            return (true, null);
        }

        return (false, "image manifest after apply does not match source manifest");
    }

    // ===================================================================
    // Local helpers
    // ===================================================================

    private static bool AssignString(Dictionary<string, JsonElement> metadata, string key, Func<string?, bool> assign)
    {
        if (!metadata.TryGetValue(key, out var v)) return false;
        string? read = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            _ => null
        };
        return assign(read);
    }

    private static DateTime? ParseNullableDate(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        if (string.IsNullOrEmpty(s)) return null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
    }

    private static string[] ReadStringArray(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var entry in v.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var s = entry.GetString();
                if (s != null)
                {
                    list.Add(s);
                }
            }
        }

        return list.ToArray();
    }

    private static T[] ReadEnumArray<T>(JsonElement v) where T : struct, Enum
    {
        if (v.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<T>();
        }

        var list = new List<T>();
        foreach (var entry in v.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var s = entry.GetString();
                if (!string.IsNullOrEmpty(s) && Enum.TryParse<T>(s, out var parsed))
                {
                    list.Add(parsed);
                }
            }
        }

        return list.ToArray();
    }

    private static Dictionary<string, string> ReadProviderIds(JsonElement v)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (v.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var prop in v.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var pv = prop.Value.GetString();
                if (!string.IsNullOrEmpty(pv))
                {
                    result[prop.Name] = pv;
                }
            }
        }

        return result;
    }

    private static string TruncateForLog(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "(empty)";
        return s.Length <= 200 ? s : string.Concat(s.AsSpan(0, 200), "…");
    }
}
