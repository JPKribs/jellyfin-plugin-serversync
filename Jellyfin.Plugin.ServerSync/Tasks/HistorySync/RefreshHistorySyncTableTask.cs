using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using TaskTriggerInfo = MediaBrowser.Model.Tasks.TaskTriggerInfo;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Source-side work item for the History refresh task: a (user mapping,
/// library mapping, source item) triple that
/// <see cref="RefreshHistorySyncTableTask.BuildRecordAsync"/> turns into a
/// <see cref="HistorySyncItem"/>.
/// </summary>
public sealed record HistoryWork(UserMapping UserMapping, LibraryMapping LibraryMapping, BaseItemDto SourceItem);

/// <summary>
/// Refresh phase for History sync. Walks every (enabled user × enabled
/// library) combination, fetches each user's library items with their
/// per-user UserData (Played/PlayCount/Position/LastPlayedDate/IsFavorite),
/// looks up the local item by translated path, and builds a
/// <see cref="HistorySyncItem"/> with the merged target state computed by
/// <see cref="HistorySyncMergeService"/>.
/// </summary>
public class RefreshHistorySyncTableTask
    : RefreshSyncTaskBase<HistorySyncItem, HistoryWork, (string SourceUserId, string SourceItemId)>
{
    private readonly HistorySyncTableService _historyService;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public RefreshHistorySyncTableTask(
        ILogger<RefreshHistorySyncTableTask> logger,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        HistorySyncTableService historyService,
        HistorySyncTableManager manager)
        : base(logger, manager, clientFactory, configManager)
    {
        _historyService = historyService;
    }

    /// <inheritdoc />
    public override string Name => "Refresh History Sync Table";

    /// <inheritdoc />
    public override string Key => "ServerSyncRefreshHistoryTable";

    /// <inheritdoc />
    public override string Description => "Scans source and local servers for watch history differences and updates the history sync table.";

    /// <inheritdoc />
    public override string Category => "History Sync";

    /// <inheritdoc />
    protected override string ModuleMutexKey => "History";

    /// <inheritdoc />
    protected override bool IsEnabled()
    {
        var config = ConfigManager.Configuration;
        if (!config.EnableHistorySync) return false;
        if (string.IsNullOrWhiteSpace(config.SourceServerUrl) || string.IsNullOrWhiteSpace(config.SourceServerApiKey)) return false;
        return config.GetEnabledUserMappings().Count > 0 && config.GetEnabledLibraryMappings().Count > 0;
    }

    /// <inheritdoc />
    protected override async Task<IList<HistoryWork>> GetListAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (Client == null)
        {
            return Array.Empty<HistoryWork>();
        }

        var config = ConfigManager.Configuration;
        var userMappings = config.UserMappings?.Where(m => m.IsEnabled).ToList() ?? new List<UserMapping>();
        var libraryMappings = config.LibraryMappings?.Where(m => m.IsEnabled).ToList() ?? new List<LibraryMapping>();

        // Pre-fetch counts per (user × library) pair for progress denominator.
        var totalExpected = 0;
        foreach (var userMapping in userMappings)
        {
            if (!Guid.TryParse(userMapping.SourceUserId, out var sourceUserId)) continue;
            foreach (var libraryMapping in libraryMappings)
            {
                if (!Guid.TryParse(libraryMapping.SourceLibraryId, out var sourceLibraryId)) continue;
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var count = await Client.GetUserLibraryItemCountAsync(sourceUserId, sourceLibraryId, cancellationToken).ConfigureAwait(false);
                    totalExpected += count;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogDebug(ex, "Failed to fetch count for user {User} library {Library}", userMapping.SourceUserName, libraryMapping.SourceLibraryName);
                }
            }
        }

        progress.Report(totalExpected > 0 ? 1 : 50);

        var work = new List<HistoryWork>();
        var fetched = 0;

        foreach (var userMapping in userMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(userMapping.SourceUserId, out var sourceUserId))
            {
                Logger.LogWarning("Skipping user mapping with invalid source ID: {Id}", userMapping.SourceUserId);
                continue;
            }

            foreach (var libraryMapping in libraryMappings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParse(libraryMapping.SourceLibraryId, out var sourceLibraryId))
                {
                    Logger.LogWarning("Skipping library mapping with invalid source ID: {Id}", libraryMapping.SourceLibraryId);
                    continue;
                }

                await PaginatedFetchUtility.FetchAllPagesAsync(
                    fetchPage: (startIndex, batchSize, ct) => Client.GetUserLibraryItemsAsync(sourceUserId, sourceLibraryId, startIndex, batchSize, ct),
                    processItem: (item, _) =>
                    {
                        work.Add(new HistoryWork(userMapping, libraryMapping, item));
                        return Task.FromResult(true);
                    },
                    libraryName: libraryMapping.SourceLibraryName,
                    sourceRootPath: libraryMapping.SourceRootPath,
                    filterMode: libraryMapping.FilterMode,
                    filteredItems: libraryMapping.FilteredItems,
                    logger: Logger,
                    cancellationToken: cancellationToken,
                    onItemProcessed: () =>
                    {
                        fetched++;
                        if (totalExpected > 0)
                        {
                            progress.Report(Math.Min(100, 100.0 * fetched / totalExpected));
                        }
                    }).ConfigureAwait(false);
            }
        }

        progress.Report(100);
        return work;
    }

    /// <inheritdoc />
    protected override Task<HistorySyncItem?> BuildRecordAsync(
        HistoryWork source,
        IReadOnlyDictionary<(string SourceUserId, string SourceItemId), HistorySyncItem> existing,
        CancellationToken cancellationToken)
    {
        var sourceItemId = source.SourceItem.Id!.Value.ToString("N", CultureInfo.InvariantCulture);
        var record = _historyService.BuildRecord(source.UserMapping, source.LibraryMapping, source.SourceItem, sourceItemId);
        return Task.FromResult(record);
    }

    /// <inheritdoc />
    protected override (string SourceUserId, string SourceItemId) ExtractKey(HistorySyncItem record)
        => (record.SourceUserId, record.SourceItemId);

    /// <inheritdoc />
    /// <remarks>
    /// In scope when both the row's library mapping AND its user mapping are
    /// currently enabled. Disabling either preserves history rows instead of
    /// pruning them, so the user's Ignored overrides survive a toggle.
    /// </remarks>
    protected override bool IsInScope(HistorySyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var config = ConfigManager.Configuration;

        var libraryEnabled = false;
        foreach (var mapping in config.GetEnabledLibraryMappings())
        {
            if (string.Equals(mapping.SourceLibraryId, record.SourceLibraryId, StringComparison.OrdinalIgnoreCase))
            {
                libraryEnabled = true;
                break;
            }
        }

        if (!libraryEnabled)
        {
            return false;
        }

        foreach (var mapping in config.GetEnabledUserMappings())
        {
            if (string.Equals(mapping.SourceUserId, record.SourceUserId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(mapping.LocalUserId, record.LocalUserId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    protected override void DecideStatus(HistorySyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Status == SyncStatus.Ignored)
        {
            return;
        }

        if (record.HasChanges)
        {
            record.Status = SyncStatus.Queued;
        }
        else
        {
            record.Status = SyncStatus.Synced;
            record.LastSyncTime = DateTime.UtcNow;
        }

        record.StatusDate = DateTime.UtcNow;
        record.Reason = null;
    }

    /// <inheritdoc />
    protected override Task FinalizeAsync(CancellationToken cancellationToken)
    {
        ConfigManager.Configuration.LastHistorySyncTime = DateTime.UtcNow;
        ConfigManager.SaveConfiguration();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(4).Ticks
        }
    };
}
