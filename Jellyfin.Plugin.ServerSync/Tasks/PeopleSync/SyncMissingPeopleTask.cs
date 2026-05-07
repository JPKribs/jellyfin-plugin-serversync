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
/// changes to the local person entities. Apply logic preserved from the
/// previous implementation — only the orchestration (status transitions,
/// error handling, progress) moved into <see cref="SyncQueueTaskBase{TRecord, TKey}"/>.
/// </summary>
public class SyncMissingPeopleTask : SyncQueueTaskBase<PeopleSyncItem, string>
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public SyncMissingPeopleTask(
        ILogger<SyncMissingPeopleTask> logger,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        ISourceServerClientFactory clientFactory,
        IPluginConfigurationManager configManager,
        PeopleSyncTableManager manager)
        : base(logger, manager, clientFactory, configManager)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
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
        var hasChanges = false;

        if (record.HasMetadataChanges)
        {
            if (await ApplyMetadataAsync(localPerson, record, cancellationToken).ConfigureAwait(false))
            {
                hasChanges = true;
            }
        }

        if (syncImages && record.HasImagesChanges && !string.IsNullOrEmpty(record.SourcePersonId))
        {
            if (!Guid.TryParse(record.SourcePersonId, out var sourceGuid))
            {
                throw new InvalidOperationException($"Invalid source person ID for {record.PersonName}: {record.SourcePersonId}");
            }

            if (Client != null && await ApplyPersonImagesAsync(localPerson, sourceGuid, Client, cancellationToken).ConfigureAwait(false))
            {
                hasChanges = true;
            }
        }

        if (hasChanges)
        {
            await localPerson.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
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

    // ===================================================================
    // Apply logic — preserved from the prior SyncMissingPeopleTask.
    // The TZ-safe DateOnlyEquals comparison for PremiereDate/EndDate is
    // unchanged from the v10.11.42 fix; do not regress to direct `!=`.
    // ===================================================================

    private Task<bool> ApplyMetadataAsync(
        BaseItem localPerson,
        PeopleSyncItem item,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.Metadata.Source))
        {
            return Task.FromResult(false);
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.Metadata.Source);
            if (metadata == null)
            {
                return Task.FromResult(false);
            }

            var hasChanges = false;

            if (metadata.TryGetValue("Name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String)
            {
                var name = nameValue.GetString();
                if (!string.IsNullOrEmpty(name) && localPerson.Name != name)
                {
                    localPerson.Name = name;
                    hasChanges = true;
                }
            }

            if (metadata.TryGetValue("OriginalTitle", out var origTitleValue) && origTitleValue.ValueKind == JsonValueKind.String)
            {
                var origTitle = origTitleValue.GetString();
                if (localPerson.OriginalTitle != origTitle)
                {
                    localPerson.OriginalTitle = origTitle;
                    hasChanges = true;
                }
            }

            if (metadata.TryGetValue("SortName", out var sortNameValue) && sortNameValue.ValueKind == JsonValueKind.String)
            {
                var sortName = sortNameValue.GetString();
                if (localPerson.SortName != sortName)
                {
                    localPerson.SortName = sortName ?? string.Empty;
                    hasChanges = true;
                }
            }

            if (metadata.TryGetValue("ForcedSortName", out var forcedSortNameValue) && forcedSortNameValue.ValueKind == JsonValueKind.String)
            {
                var forcedSortName = forcedSortNameValue.GetString();
                if (localPerson.ForcedSortName != forcedSortName)
                {
                    localPerson.ForcedSortName = forcedSortName;
                    hasChanges = true;
                }
            }

            if (metadata.TryGetValue("Overview", out var overviewValue) && overviewValue.ValueKind == JsonValueKind.String)
            {
                var overview = overviewValue.GetString();
                if (localPerson.Overview != overview)
                {
                    localPerson.Overview = overview;
                    hasChanges = true;
                }
            }

            // PremiereDate (birth date — date-only semantic; compare by calendar
            // date to avoid timezone-driven false diffs across servers).
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

                if (!JsonComparisonUtility.DateOnlyEquals(localPerson.PremiereDate, premiereDate))
                {
                    localPerson.PremiereDate = premiereDate;
                    hasChanges = true;
                }
            }

            // EndDate (death date — date-only semantic).
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

                if (!JsonComparisonUtility.DateOnlyEquals(localPerson.EndDate, endDate))
                {
                    localPerson.EndDate = endDate;
                    hasChanges = true;
                }
            }

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

            // ProductionLocations (birth place — string array, typically one entry).
            if (metadata.TryGetValue("ProductionLocations", out var locationsValue) && locationsValue.ValueKind == JsonValueKind.Array)
            {
                var locations = new List<string>();
                foreach (var loc in locationsValue.EnumerateArray())
                {
                    if (loc.ValueKind == JsonValueKind.String)
                    {
                        var s = loc.GetString();
                        if (!string.IsNullOrEmpty(s))
                        {
                            locations.Add(s);
                        }
                    }
                }

                var locationsArray = locations.ToArray();
                if (!localPerson.ProductionLocations.SequenceEqual(locationsArray, StringComparer.Ordinal))
                {
                    localPerson.ProductionLocations = locationsArray;
                    hasChanges = true;
                }
            }

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

                var tagsArray = tags.ToArray();
                if (!localPerson.Tags.SequenceEqual(tagsArray, StringComparer.Ordinal))
                {
                    localPerson.Tags = tagsArray;
                    hasChanges = true;
                }
            }

            if (metadata.TryGetValue("ProviderIds", out var providerIdsValue) && providerIdsValue.ValueKind == JsonValueKind.Object)
            {
                // Reconcile providers — see same fix in SyncMissingMetadataTask.
                // Without the cleanup pass, local-only entries persist and
                // make the row report "still desynced" forever.
                var sourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in providerIdsValue.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrEmpty(prop.Value.GetString()))
                    {
                        sourceKeys.Add(prop.Name);
                    }
                }

                if (localPerson.ProviderIds != null)
                {
                    var toRemove = localPerson.ProviderIds.Keys
                        .Where(k => !sourceKeys.Contains(k))
                        .ToList();
                    foreach (var key in toRemove)
                    {
                        localPerson.ProviderIds.Remove(key);
                        hasChanges = true;
                    }
                }

                foreach (var prop in providerIdsValue.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var providerValue = prop.Value.GetString();
                        if (!string.IsNullOrEmpty(providerValue) && localPerson.GetProviderId(prop.Name) != providerValue)
                        {
                            localPerson.SetProviderId(prop.Name, providerValue);
                            hasChanges = true;
                        }
                    }
                }
            }

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

                var lockedFieldsArray = lockedFieldsList.ToArray();
                if (!localPerson.LockedFields.SequenceEqual(lockedFieldsArray))
                {
                    localPerson.LockedFields = lockedFieldsArray;
                    hasChanges = true;
                }
            }

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

            return Task.FromResult(hasChanges);
        }
        catch (JsonException ex)
        {
            Logger.LogError(ex, "Failed to parse metadata JSON for person {PersonName}", item.PersonName);
            return Task.FromResult(false);
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
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (IOException)
                    {
                        // Best-effort cleanup.
                    }
                }
            }

            return appliedAny;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to apply images for person {PersonName}", localPerson.Name);
            return false;
        }
    }
}
