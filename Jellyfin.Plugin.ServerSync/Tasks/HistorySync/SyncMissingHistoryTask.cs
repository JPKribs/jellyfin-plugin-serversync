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
/// rows, applies the merged play-state to the local server via
/// <see cref="LocalServerClient.UpdateUserItemData"/>, then verifies the
/// write by re-reading the user-data and confirming the merged values
/// landed. A row that fails verification is recorded as Errored with a
/// precise <see cref="Models.Common.SyncRecord.Reason"/> rather than being
/// silently marked Synced.
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

        // Verify the write took. Re-read the user-data and compare against
        // what we just merged. Mismatches surface as a precise Errored
        // reason rather than a silent green badge.
        var (ok, reason) = VerifyApplied(record, localUserId, localItemId);
        if (!ok)
        {
            throw new InvalidOperationException(reason ?? "verification failed");
        }

        Logger.LogInformation("Apply History verified for {ItemName}: {Changes}",
            record.ItemName,
            HistorySyncMergeService.GetChangeSummary(record));

        return Task.CompletedTask;
    }

    private (bool Succeeded, string? FailureReason) VerifyApplied(HistorySyncItem record, Guid localUserId, Guid localItemId)
    {
        var fresh = _localClient.GetUserItemData(localUserId, localItemId);
        if (fresh == null)
        {
            return (false, "user-data row not found after apply");
        }

        var diffs = new List<string>();

        if (record.MergedIsPlayed.HasValue && fresh.Played != record.MergedIsPlayed.Value)
        {
            diffs.Add($"Played wanted={record.MergedIsPlayed.Value}, got={fresh.Played}");
        }

        if (record.MergedPlayCount.HasValue && fresh.PlayCount != record.MergedPlayCount.Value)
        {
            diffs.Add($"PlayCount wanted={record.MergedPlayCount.Value}, got={fresh.PlayCount}");
        }

        if (record.MergedPlaybackPositionTicks.HasValue && fresh.PlaybackPositionTicks != record.MergedPlaybackPositionTicks.Value)
        {
            diffs.Add($"Position wanted={record.MergedPlaybackPositionTicks.Value}, got={fresh.PlaybackPositionTicks}");
        }

        if (record.MergedIsFavorite.HasValue && fresh.IsFavorite != record.MergedIsFavorite.Value)
        {
            diffs.Add($"Favorite wanted={record.MergedIsFavorite.Value}, got={fresh.IsFavorite}");
        }

        // LastPlayedDate is harder — comparing nullable DateTime across
        // serialization round-trips can show sub-second drift. Compare to
        // second resolution only.
        if (record.MergedLastPlayedDate.HasValue)
        {
            var wantSec = TruncateToSecond(record.MergedLastPlayedDate.Value);
            var gotSec = fresh.LastPlayedDate.HasValue ? TruncateToSecond(fresh.LastPlayedDate.Value) : (DateTime?)null;
            if (gotSec != wantSec)
            {
                diffs.Add($"LastPlayedDate wanted={wantSec:o}, got={gotSec:o}");
            }
        }

        if (diffs.Count > 0)
        {
            return (false, $"verification mismatch: {string.Join("; ", diffs)}");
        }

        return (true, null);
    }

    private static DateTime TruncateToSecond(DateTime dt)
    {
        return new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second, dt.Kind);
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
