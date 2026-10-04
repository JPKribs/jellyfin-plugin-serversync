using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Applies one history hint: reads the user's live history for the item from the origin, builds the
/// same row the full scan would, merges three way against the last agreed base, settles the merged
/// state with the origin through the negotiation endpoint, and writes it here under the apply guard
/// so the write raises no hint of its own. A merge that moved past what the origin had is a change of
/// this server's own, so it is published to the other peers.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class HistoryHintHandler
{
    private readonly HistorySyncTableService _tableService;
    private readonly HistorySyncTableManager _table;
    private readonly LocalServerClient _localClient;
    private readonly ApplyGuard _guard;
    private readonly VersionStore _versions;
    private readonly LocalHintPublisher _publisher;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<HistoryHintHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HistoryHintHandler"/> class.
    /// </summary>
    /// <param name="tableService">Builds history rows.</param>
    /// <param name="table">The history table.</param>
    /// <param name="localClient">This server's library and user data.</param>
    /// <param name="guard">The apply guard.</param>
    /// <param name="versions">The version store.</param>
    /// <param name="publisher">Publishes changes of this server's own to the other peers.</param>
    /// <param name="applicationHost">The server host, for this server's id.</param>
    /// <param name="logger">Logger.</param>
    public HistoryHintHandler(
        HistorySyncTableService tableService,
        HistorySyncTableManager table,
        LocalServerClient localClient,
        ApplyGuard guard,
        VersionStore versions,
        LocalHintPublisher publisher,
        IServerApplicationHost applicationHost,
        ILogger<HistoryHintHandler> logger)
    {
        _tableService = tableService;
        _table = table;
        _localClient = localClient;
        _guard = guard;
        _versions = versions;
        _publisher = publisher;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <summary>Applies one hint from one origin.</summary>
    /// <param name="hint">The inbound row.</param>
    /// <param name="origin">The configured entry for the origin.</param>
    /// <param name="client">A client bound to the origin.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async Task<HintApplyResult> ApplyAsync(InboundHint hint, SourceServer origin, SourceServerClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hint);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(client);

        if (!HintProtocol.TryParseHistoryKey(hint.Key, out var originUserId, out var originItemId))
        {
            return HintApplyResult.Dropped("the hint's key is not a user id and item id");
        }

        var userMapping = HintMapping.FindBySourceUser(origin, originUserId);
        if (userMapping is null)
        {
            return HintApplyResult.Dropped($"user '{hint.UserName ?? hint.UserId}' on '{origin.DisplayName}' is not mapped here");
        }

        var libraryMapping = HintMapping.FindBySourcePath(origin, hint.ItemPath);
        if (libraryMapping is null)
        {
            return HintApplyResult.Dropped($"path '{hint.ItemPath}' on '{origin.DisplayName}' is not in a mapped library here");
        }

        var page = await client.GetItemsWithUserDataByIdsAsync(originUserId, new Guid?[] { originItemId }, cancellationToken).ConfigureAwait(false);
        if (page is null)
        {
            return HintApplyResult.RetryLater($"could not read the item's history from '{origin.DisplayName}'");
        }

        var dto = page.Items?.FirstOrDefault();
        if (dto is null || !dto.Id.HasValue)
        {
            return HintApplyResult.Dropped($"the item no longer exists on '{origin.DisplayName}'");
        }

        var sourceItemId = dto.Id.Value.ToString("N", CultureInfo.InvariantCulture);
        var record = _tableService.BuildRecord(userMapping, libraryMapping, dto, sourceItemId, negotiateWithSource: true);
        if (record is null)
        {
            return HintApplyResult.Dropped("no local item at the mapped path yet; the next content sync or scan will pick it up");
        }

        if (!Guid.TryParse(record.LocalUserId, out var localUserId) || !Guid.TryParse(record.LocalItemId, out var localItemId))
        {
            return HintApplyResult.Dropped("the local user or item id is not valid");
        }

        var localKey = HintProtocol.HistoryKey(localUserId, localItemId);
        var now = DateTime.UtcNow;

        record.ServerKey = origin.Key;
        var previous = _table.GetByKey((record.SourceUserId, record.SourceItemId));
        if (previous is not null)
        {
            if (previous.Status == SyncStatus.Ignored)
            {
                return HintApplyResult.Dropped("the row is ignored on this server");
            }

            record.Id = previous.Id;
            record.CarryNegotiatedBaseFrom(previous);
            if (record.HasNegotiatedBase)
            {
                HistorySyncMergeService.MergeHistoryData(record);
            }
        }

        if (!record.HasNegotiatedBase)
        {
            DecideFirstContact(record, hint, localKey);
        }

        if (!HistorySyncMergeService.HasChangesToSync(record))
        {
            // Both servers already hold the merged state, so this becomes the agreed base and the hint's
            // version is adopted as the version of the value here.
            record.RecordNegotiatedBase(now);
            Store(record, now);
            _versions.Set(new ObjectVersion { Kind = HintKind.History, Key = localKey, ServerId = hint.VersionServerId, Timestamp = hint.VersionTimestamp });
            return HintApplyResult.Unchanged;
        }

        // A key that is not an administrator's cannot negotiate, since the origin's Server Sync
        // endpoints refuse it. The merged state is then written here only, the way one way history
        // always worked, and no base is recorded because the origin never agreed to anything.
        var oneWay = false;
        NegotiationStep settled;
        try
        {
            settled = await SettleWithOriginAsync(record, client, cancellationToken).ConfigureAwait(false);
        }
        catch (PeerRefusedException ex) when (ex.KeyRefused)
        {
            oneWay = true;
            settled = new NegotiationStep(NegotiationAction.Proceed, null);
            _logger.LogWarning("The key for '{Origin}' is not an administrator's, so history from it is applied one way", origin.DisplayName);
        }

        if (settled.Action == NegotiationAction.Fail)
        {
            return HintApplyResult.RetryLater(settled.Reason ?? "negotiation with the origin failed");
        }

        // Whether the merge moved past what the origin held is decided now, before the row's picture of
        // the origin is brought up to date below.
        var movedPastOrigin = HistorySyncMergeService.SourceDiffersFromMerged(record);

        using (_guard.Enter(HintProtocol.GuardKey(HintKind.History, localKey)))
        {
            var written = _localClient.UpdateUserItemData(
                localUserId,
                localItemId,
                record.MergedIsPlayed,
                record.MergedPlayCount,
                record.MergedPlaybackPositionTicks,
                record.MergedLastPlayedDate,
                record.MergedIsFavorite,
                clearLastPlayedDate: record.MergedIsPlayed == false);
            if (!written)
            {
                return HintApplyResult.RetryLater("the local user data write failed");
            }
        }

        var fresh = _localClient.GetUserItemData(localUserId, localItemId);
        if (fresh is null || !PeerHistoryNegotiator.StatesMatch(PeerHistoryNegotiator.FromUserData(fresh), PeerHistoryNegotiator.MergedStateOf(record)))
        {
            return HintApplyResult.RetryLater("the local write did not land as merged");
        }

        record.LocalIsPlayed = record.MergedIsPlayed;
        record.LocalPlayCount = record.MergedPlayCount;
        record.LocalPlaybackPositionTicks = record.MergedPlaybackPositionTicks;
        record.LocalLastPlayedDate = record.MergedLastPlayedDate;
        record.LocalIsFavorite = record.MergedIsFavorite;
        if (!oneWay)
        {
            PeerHistoryNegotiator.ApplySourceState(record, PeerHistoryNegotiator.MergedStateOf(record));
            record.UpdateSourceStateBundle();
            record.RecordNegotiatedBase(now);
        }

        Store(record, now);

        if (movedPastOrigin)
        {
            // This server contributed to the merged state, so the other peers hear about it from here.
            // The origin already holds it through the negotiation above.
            var version = new ObjectVersion { ServerId = _applicationHost.SystemId, Timestamp = now };
            _publisher.PublishHistory(localUserId, userMapping.LocalUserName, localItemId, record.LocalPath ?? string.Empty, version, excludePeerKey: origin.Key);
        }
        else
        {
            _versions.Set(new ObjectVersion { Kind = HintKind.History, Key = localKey, ServerId = hint.VersionServerId, Timestamp = hint.VersionTimestamp });
        }

        _logger.LogInformation("Applied a history hint from '{Origin}' for {Item}: {Changes}", origin.DisplayName, record.ItemName, HistorySyncMergeService.GetChangeSummary(record));
        return HintApplyResult.Applied;
    }

    /// <summary>
    /// The two servers have never agreed on this object, so the three way merge has no base and cannot
    /// tell an item that was never watched from one that was deliberately unmarked. When this server
    /// knows when its own value was last edited, the newer edit wins outright on origin versions, as
    /// the plan requires. Without a local version the two way merge stands, which keeps a play that
    /// predates versioning rather than risk losing it, and the base recorded after this apply makes
    /// every later merge three way.
    /// </summary>
    private void DecideFirstContact(HistorySyncItem record, InboundHint hint, string localKey)
    {
        var localVersion = _versions.Get(HintKind.History, localKey);
        if (localVersion is null)
        {
            return;
        }

        var incoming = new ObjectVersion { Kind = HintKind.History, Key = localKey, ServerId = hint.VersionServerId, Timestamp = hint.VersionTimestamp };
        var source = PeerHistoryNegotiator.SourceStateOf(record);
        var local = new PeerHistoryState
        {
            Played = record.LocalIsPlayed,
            PlayCount = record.LocalPlayCount,
            PlaybackPositionTicks = record.LocalPlaybackPositionTicks,
            LastPlayedDate = record.LocalLastPlayedDate,
            IsFavorite = record.LocalIsFavorite
        };

        switch (VersionDecider.Decide(localVersion, incoming, PeerHistoryNegotiator.StatesMatch(source, local)))
        {
            case VersionDecision.Apply:
                record.MergedIsPlayed = record.SourceIsPlayed;
                record.MergedPlayCount = record.SourcePlayCount;
                record.MergedPlaybackPositionTicks = record.SourcePlaybackPositionTicks;
                record.MergedLastPlayedDate = record.SourceLastPlayedDate;
                record.MergedIsFavorite = record.SourceIsFavorite;
                _logger.LogDebug("First contact for {Item}: the origin's edit is newer, taking it", record.ItemName);
                break;

            case VersionDecision.Keep:
                record.MergedIsPlayed = record.LocalIsPlayed;
                record.MergedPlayCount = record.LocalPlayCount;
                record.MergedPlaybackPositionTicks = record.LocalPlaybackPositionTicks;
                record.MergedLastPlayedDate = record.LocalLastPlayedDate;
                record.MergedIsFavorite = record.LocalIsFavorite;
                _logger.LogDebug("First contact for {Item}: this server's edit is newer, keeping it", record.ItemName);
                break;

            default:
                break;
        }
    }

    private async Task<NegotiationStep> SettleWithOriginAsync(HistorySyncItem record, SourceServerClient client, CancellationToken cancellationToken)
    {
        var answer = await OfferAsync(record, client, cancellationToken).ConfigureAwait(false);
        var step = PeerHistoryNegotiator.ResolveOutcome(record, answer, allowRetry: true);
        if (step.Action == NegotiationAction.Retry)
        {
            answer = await OfferAsync(record, client, cancellationToken).ConfigureAwait(false);
            step = PeerHistoryNegotiator.ResolveOutcome(record, answer, allowRetry: false);
        }

        return step;
    }

    private async Task<PeerHistoryResult> OfferAsync(HistorySyncItem record, SourceServerClient client, CancellationToken cancellationToken)
    {
        var request = new PeerHistoryRequest
        {
            SenderServerId = _applicationHost.SystemId,
            Items =
            {
                new PeerHistoryEntry
                {
                    UserId = record.SourceUserId,
                    ItemId = record.SourceItemId,
                    Expected = PeerHistoryNegotiator.SourceStateOf(record),
                    Proposed = PeerHistoryNegotiator.MergedStateOf(record),
                    SenderUserId = record.LocalUserId,
                    SenderItemId = record.LocalItemId
                }
            }
        };

        try
        {
            var response = await client.NegotiateHistoryAsync(request, cancellationToken).ConfigureAwait(false);
            return response.Items.Count > 0
                ? response.Items[0]
                : new PeerHistoryResult { Outcome = PeerHistoryOutcome.Failed, Reason = "origin returned no result" };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PeerRefusedException ex) when (ex.KeyRefused)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PeerHistoryResult { Outcome = PeerHistoryOutcome.Failed, Reason = ex.Message };
        }
    }

    private void Store(HistorySyncItem record, DateTime now)
    {
        record.Status = SyncStatus.Synced;
        record.StatusDate = now;
        record.LastSyncTime = now;
        record.Reason = null;
        record.RetryCount = 0;
        record.MarkSynced();
        _table.Upsert(record);
    }
}
