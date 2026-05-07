using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using TaskTriggerInfo = MediaBrowser.Model.Tasks.TaskTriggerInfo;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Apply phase for History sync. Reads queued <see cref="HistorySyncItem"/>
/// rows and applies their merged play-state to the local server via
/// <see cref="LocalServerClient.UpdateUserItemData"/>.
/// </summary>
public class SyncMissingHistoryTask
    : SyncQueueTaskBase<HistorySyncItem, (string SourceUserId, string SourceItemId)>
{
    private readonly LocalServerClient _localClient;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public SyncMissingHistoryTask(
        ILogger<SyncMissingHistoryTask> logger,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        LocalServerClient localClient,
        HistorySyncTableManager manager)
        : base(logger, manager, clientFactory, configManager)
    {
        _localClient = localClient;
    }

    /// <inheritdoc />
    public override string Name => "Sync History";

    /// <inheritdoc />
    public override string Key => "ServerSyncMissingHistory";

    /// <inheritdoc />
    public override string Description => "Applies queued watch history changes from the sync table to the local server.";

    /// <inheritdoc />
    public override string Category => "History Sync";

    /// <inheritdoc />
    protected override string ModuleMutexKey => "History";

    /// <inheritdoc />
    protected override bool IsEnabled()
    {
        var config = ConfigManager.Configuration;
        return config.EnableHistorySync
            && !string.IsNullOrWhiteSpace(config.SourceServerUrl)
            && !string.IsNullOrWhiteSpace(config.SourceServerApiKey);
    }

    /// <inheritdoc />
    protected override Task ApplyAsync(HistorySyncItem record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrEmpty(record.LocalItemId))
        {
            throw new InvalidOperationException("Local item not found");
        }

        if (string.IsNullOrEmpty(record.LocalUserId))
        {
            throw new InvalidOperationException("Local user not found");
        }

        if (!Guid.TryParse(record.LocalUserId, out var localUserId)
            || !Guid.TryParse(record.LocalItemId, out var localItemId))
        {
            throw new InvalidOperationException("Invalid user or item ID");
        }

        var success = _localClient.UpdateUserItemData(
            localUserId,
            localItemId,
            record.MergedIsPlayed,
            record.MergedPlayCount,
            record.MergedPlaybackPositionTicks,
            record.MergedLastPlayedDate,
            record.MergedIsFavorite);

        if (!success)
        {
            throw new InvalidOperationException("Failed to update user data");
        }

        Logger.LogDebug(
            "Synced history for {ItemName}: {Changes}",
            record.ItemName,
            HistorySyncMergeService.GetChangeSummary(record));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// HistorySyncItem has no SyncableValue fields (its base MarkSynced is
    /// a no-op), so on success we copy the merged values into the local
    /// snapshot. The next Refresh will re-pull the actual local state, but
    /// in the meantime <see cref="HistorySyncMergeService.HasChangesToSync"/>
    /// will see local == merged and not requeue.
    /// </remarks>
    protected override void OnApplySucceeded(HistorySyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.LocalIsPlayed = record.MergedIsPlayed;
        record.LocalPlayCount = record.MergedPlayCount;
        record.LocalPlaybackPositionTicks = record.MergedPlaybackPositionTicks;
        record.LocalLastPlayedDate = record.MergedLastPlayedDate;
        record.LocalIsFavorite = record.MergedIsFavorite;
        record.MarkSynced();
    }

    /// <inheritdoc />
    protected override Task FinalizeAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = ConfigManager.Configuration;
        config.LastHistorySyncTime = DateTime.UtcNow;
        ConfigManager.SaveConfiguration();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks
        }
    };
}
