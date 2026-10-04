using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using MediaBrowser.Controller;
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
/// When the configuration asks for negotiation, the merged state is first offered to the source
/// server's own Server Sync plugin, and the local write only happens once the source has accepted it.
/// </summary>
public class SyncMissingHistoryTask
    : SyncQueueTaskBase<HistorySyncItem, (string SourceUserId, string SourceItemId)>
{
    private readonly LocalServerClient _localClient;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ApplyGuard? _guard;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public SyncMissingHistoryTask(
        ILogger<SyncMissingHistoryTask> logger,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        LocalServerClient localClient,
        HistorySyncTableManager manager,
        IServerApplicationHost applicationHost,
        ApplyGuard? guard = null)
        : base(logger, manager, clientFactory, configManager)
    {
        _localClient = localClient;
        _applicationHost = applicationHost;
        _guard = guard;
    }

    // A scheduled history write is a copy, not an edit made here. Registered with the guard so the
    // change observer does not turn the save event into a hint back to every peer, which would also
    // record this server as the editor of a value it only received.
    /// <inheritdoc />
    protected override IDisposable? EnterApplyGuard(HistorySyncItem record)
    {
        if (_guard is null || record is null
            || !Guid.TryParse(record.LocalUserId, out var localUserId)
            || !Guid.TryParse(record.LocalItemId, out var localItemId))
        {
            return null;
        }

        return _guard.Enter(HintProtocol.GuardKey(HintKind.History, HintProtocol.HistoryKey(localUserId, localItemId)));
    }

    // Answers from the group offer, consumed by ApplyAsync one row at a time. Keyed by reference
    // because the same row instance flows from GetApplyGroups through PrepareGroupAsync to ApplyAsync.
    private readonly ConcurrentDictionary<HistorySyncItem, PeerHistoryResult> _peerAnswers = new(ReferenceEqualityComparer.Instance);

    // Servers that cannot negotiate this run, by entry key, with the reason their rows will carry.
    private readonly Dictionary<string, string> _negotiationBlocked = new(StringComparer.OrdinalIgnoreCase);

    // Servers whose key is not an administrator's. Their Server Sync endpoints refuse it, so history
    // from them is applied one way, the way it was before negotiation existed, and no base is recorded.
    private readonly HashSet<string> _oneWay = new(StringComparer.OrdinalIgnoreCase);

    private bool Negotiating => ConfigManager.Configuration.HistorySyncNegotiate;

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
        return config.EnableHistorySync && config.GetPullServers().Count > 0;
    }

    /// <summary>
    /// Confirms the source runs a Server Sync that can negotiate history before any row is touched,
    /// so a missing plugin on the source shows up as one clear run failure rather than a row of
    /// identical errors.
    /// </summary>
    /// <inheritdoc />
    protected override async Task<bool> BeforeRunAsync(CancellationToken cancellationToken)
    {
        if (!await base.BeforeRunAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (!Negotiating)
        {
            return true;
        }

        // Every connected server is asked whether it can negotiate. One that cannot is remembered so
        // its rows error with the reason instead of being written one sided, and the run only aborts
        // when no server at all can negotiate.
        _peerAnswers.Clear();
        _negotiationBlocked.Clear();
        _oneWay.Clear();
        foreach (var source in Sources)
        {
            PeerCapabilities? capabilities;
            try
            {
                capabilities = await source.Client.GetPeerCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                _oneWay.Add(source.Key);
                Logger.LogWarning("{Task}: the key for '{Server}' is not an administrator's, so its Server Sync cannot be asked to negotiate. History from it is applied one way", Name, source.Name);
                continue;
            }
            catch (Exception ex)
            {
                _negotiationBlocked[source.Key] = $"could not reach the Server Sync plugin on '{source.Name}': {ex.Message}";
                continue;
            }

            if (capabilities is null)
            {
                _negotiationBlocked[source.Key] = $"'{source.Name}' does not run Server Sync, or its version predates two way history. Install or update Server Sync there, or turn off Negotiate With Source Server";
                continue;
            }

            if (!capabilities.Features.Contains(PeerHistoryNegotiator.HistoryFeature))
            {
                _negotiationBlocked[source.Key] = $"Server Sync {capabilities.PluginVersion} on '{source.Name}' does not support two way history";
                continue;
            }

            Logger.LogInformation("{Task}: negotiating history with Server Sync {Version} on '{Server}'", Name, capabilities.PluginVersion, source.Name);
        }

        if (_negotiationBlocked.Count == Sources.Count)
        {
            FailPreflight(string.Join("; ", _negotiationBlocked.Values));
            return false;
        }

        foreach (var reason in _negotiationBlocked.Values)
        {
            Logger.LogError("{Task}: {Reason}", Name, reason);
        }

        return true;
    }

    /// <inheritdoc />
    protected override async Task ApplyAsync(HistorySyncItem record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var (localUserId, localItemId) = ParseLocalIds(record);

        if (Negotiating)
        {
            await NegotiateWithSourceAsync(record, cancellationToken).ConfigureAwait(false);
        }

        var success = _localClient.UpdateUserItemData(
            localUserId,
            localItemId,
            record.MergedIsPlayed,
            record.MergedPlayCount,
            record.MergedPlaybackPositionTicks,
            record.MergedLastPlayedDate,
            record.MergedIsFavorite,
            clearLastPlayedDate: record.MergedIsPlayed == false);

        if (!success)
        {
            throw new InvalidOperationException("Failed to update user data");
        }
    }

    /// <summary>
    /// Offers a whole group to the source in one request. The peer's answer for each row is kept for
    /// <see cref="ApplyAsync(HistorySyncItem, CancellationToken)"/> to consume, so a large first sync
    /// costs a few hundred round trips rather than one per row. A transport failure is recorded
    /// against every row in the group and surfaces as that row's error.
    /// </summary>
    /// <inheritdoc />
    protected override async Task PrepareGroupAsync(IList<HistorySyncItem> group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);

        if (!Negotiating || group.Count == 0)
        {
            return;
        }

        // Groups never mix servers, see GetApplyGroups, so the first row's source serves the group.
        var groupSource = SourceFor(group[0]);
        if (groupSource is null || _negotiationBlocked.ContainsKey(groupSource.Key) || _oneWay.Contains(groupSource.Key))
        {
            return;
        }

        var request = new PeerHistoryRequest { SenderServerId = _applicationHost.SystemId };
        foreach (var record in group)
        {
            request.Items.Add(EntryFor(record));
        }

        try
        {
            var response = await groupSource.Client.NegotiateHistoryAsync(request, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < group.Count; i++)
            {
                _peerAnswers[group[i]] = i < response.Items.Count
                    ? response.Items[i]
                    : new PeerHistoryResult { Outcome = PeerHistoryOutcome.Failed, Reason = "source returned no result for this row" };
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            foreach (var record in group)
            {
                _peerAnswers[record] = new PeerHistoryResult { Outcome = PeerHistoryOutcome.Failed, Reason = ex.Message };
            }
        }
    }

    /// <summary>
    /// Settles one row with the source before it is written locally. The group answer is consumed
    /// first. A stale answer means the source moved since refresh: the row is merged again against the
    /// source's live state and offered once more on its own. A second refusal, a missing item, or a
    /// failed write on the source errors the row with the source's reason, and the local write is
    /// skipped so the two servers never diverge further.
    /// </summary>
    private async Task NegotiateWithSourceAsync(HistorySyncItem record, CancellationToken cancellationToken)
    {
        var source = RequireSource(record);
        if (_negotiationBlocked.TryGetValue(source.Key, out var blocked))
        {
            throw new InvalidOperationException(blocked);
        }

        if (_oneWay.Contains(source.Key))
        {
            return;
        }

        if (!_peerAnswers.TryRemove(record, out var answer))
        {
            // Not prepared as part of a group, so ask for this row alone.
            answer = await OfferAloneAsync(record, cancellationToken).ConfigureAwait(false);
        }

        var step = PeerHistoryNegotiator.ResolveOutcome(record, answer, allowRetry: true);
        if (step.Action == NegotiationAction.Retry)
        {
            Logger.LogInformation("Source state for {ItemName} changed since refresh, merging again", record.ItemName);
            answer = await OfferAloneAsync(record, cancellationToken).ConfigureAwait(false);
            step = PeerHistoryNegotiator.ResolveOutcome(record, answer, allowRetry: false);
        }

        if (step.Action == NegotiationAction.Fail)
        {
            throw new InvalidOperationException(step.Reason ?? "negotiation failed");
        }
    }

    private async Task<PeerHistoryResult> OfferAloneAsync(HistorySyncItem record, CancellationToken cancellationToken)
    {
        var request = new PeerHistoryRequest
        {
            SenderServerId = _applicationHost.SystemId,
            Items = { EntryFor(record) }
        };

        var response = await RequireSource(record).Client.NegotiateHistoryAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Items.Count > 0
            ? response.Items[0]
            : new PeerHistoryResult { Outcome = PeerHistoryOutcome.Failed, Reason = "source returned no result for this row" };
    }

    private static PeerHistoryEntry EntryFor(HistorySyncItem record) => new()
    {
        UserId = record.SourceUserId,
        ItemId = record.SourceItemId,
        Expected = PeerHistoryNegotiator.SourceStateOf(record),
        Proposed = PeerHistoryNegotiator.MergedStateOf(record),
        SenderUserId = record.LocalUserId,
        SenderItemId = record.LocalItemId
    };

    /// <summary>
    /// When negotiating, rows are offered to the source in batches no larger than the peer endpoint
    /// accepts. One way mode keeps the base behavior.
    /// </summary>
    /// <inheritdoc />
    protected override IEnumerable<IList<HistorySyncItem>> GetApplyGroups(IList<HistorySyncItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (!Negotiating)
        {
            foreach (var group in base.GetApplyGroups(items))
            {
                yield return group;
            }

            yield break;
        }

        // Groups never mix servers: each batch is offered to the server its rows came from.
        foreach (var byServer in items.GroupBy(i => i.ServerKey ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var rows = byServer.ToList();
            for (var offset = 0; offset < rows.Count; offset += PeerHistoryNegotiator.MaxEntriesPerRequest)
            {
                var size = Math.Min(PeerHistoryNegotiator.MaxEntriesPerRequest, rows.Count - offset);
                yield return rows.GetRange(offset, size);
            }
        }
    }

    /// <inheritdoc />
    protected override Task VerifyAfterApplyAsync(HistorySyncItem record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var (localUserId, localItemId) = ParseLocalIds(record);

        // Re-read the user-data and compare against what we just merged.
        // Mismatches surface as a precise Errored reason rather than a
        // silent green badge.
        var (ok, reason) = VerifyApplied(record, localUserId, localItemId);
        if (!ok)
        {
            throw new InvalidOperationException(reason ?? "verification failed");
        }

        // The summary only names local changes. In two way mode the row may have queued for the
        // source's benefit alone, so say that the source was written too.
        Logger.LogInformation("Apply History verified for {ItemName}: {Changes}{Peer}",
            record.ItemName,
            HistorySyncMergeService.GetChangeSummary(record),
            Negotiating ? " (source updated as well)" : string.Empty);

        return Task.CompletedTask;
    }

    private static (Guid LocalUserId, Guid LocalItemId) ParseLocalIds(HistorySyncItem record)
    {
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

        return (localUserId, localItemId);
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

        // LastPlayedDate is harder — Jellyfin does not round-trip sub-second
        // precision. Shares its definition of "the same instant" with
        // HasChangesToSync, so the change detector and this verifier can never
        // disagree about whether the write landed.
        if (record.MergedLastPlayedDate.HasValue
            && !HistorySyncMergeService.SameInstantToSecond(record.MergedLastPlayedDate, fresh.LastPlayedDate))
        {
            diffs.Add(
                $"LastPlayedDate wanted={HistorySyncMergeService.TruncateToSecond(record.MergedLastPlayedDate.Value):o}, "
                + $"got={(fresh.LastPlayedDate.HasValue ? HistorySyncMergeService.TruncateToSecond(fresh.LastPlayedDate.Value).ToString("o", System.Globalization.CultureInfo.InvariantCulture) : "null")}");
        }

        if (record.MergedIsPlayed == false && !record.MergedLastPlayedDate.HasValue && fresh.LastPlayedDate.HasValue)
        {
            diffs.Add($"LastPlayedDate wanted=null, got={fresh.LastPlayedDate.Value:o}");
        }

        if (diffs.Count > 0)
        {
            return (false, $"verification mismatch: {string.Join("; ", diffs)}");
        }

        return (true, null);
    }

    // HistorySyncItem has no SyncableValue fields (its base MarkSynced is
    // a no-op), so on success we copy the merged values into the local
    // snapshot. The next Refresh will re-pull the actual local state, but
    // in the meantime <see cref="HistorySyncMergeService.HasChangesToSync"/>
    // will see local == merged and not requeue.
    /// <inheritdoc />
    protected override void OnApplySucceeded(HistorySyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.LocalIsPlayed = record.MergedIsPlayed;
        record.LocalPlayCount = record.MergedPlayCount;
        record.LocalPlaybackPositionTicks = record.MergedPlaybackPositionTicks;
        record.LocalLastPlayedDate = record.MergedLastPlayedDate;
        record.LocalIsFavorite = record.MergedIsFavorite;

        // Both servers now hold the merged state, so it becomes the base the next merge compares
        // against, and the row's picture of the source is brought up to date so the table and any
        // manual retry before the next refresh describe the source as it now is. Only recorded when
        // negotiating, so a one way install keeps its old merge rules.
        if (Negotiating && !_oneWay.Contains(record.ServerKey ?? string.Empty))
        {
            PeerHistoryNegotiator.ApplySourceState(record, PeerHistoryNegotiator.MergedStateOf(record));
            record.UpdateSourceStateBundle();
            record.RecordNegotiatedBase(DateTime.UtcNow);
        }

        record.MarkSynced();
    }

    /// <inheritdoc />
    protected override void RecordRunCompleted(Jellyfin.Plugin.ServerSync.Configuration.PluginConfiguration config, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.LastHistorySyncTime = utcNow;
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
