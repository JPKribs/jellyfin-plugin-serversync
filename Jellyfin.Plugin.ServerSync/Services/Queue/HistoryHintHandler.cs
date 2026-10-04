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
    private readonly MediaBrowser.Controller.Library.ILibraryManager _libraryManager;
    private readonly LocalHintPublisher _publisher;
    private readonly IServerApplicationHost _applicationHost;
    private readonly IPluginConfigurationManager _configManager;
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
    /// <param name="configManager">Plugin configuration, for the history switch.</param>
    /// <param name="libraryManager">The library, which names the item the activity log mentions.</param>
    /// <param name="logger">Logger.</param>
    public HistoryHintHandler(
        HistorySyncTableService tableService,
        HistorySyncTableManager table,
        LocalServerClient localClient,
        ApplyGuard guard,
        VersionStore versions,
        LocalHintPublisher publisher,
        IServerApplicationHost applicationHost,
        IPluginConfigurationManager configManager,
        MediaBrowser.Controller.Library.ILibraryManager libraryManager,
        ILogger<HistoryHintHandler> logger)
    {
        _configManager = configManager;
        _libraryManager = libraryManager;
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

        if (!_configManager.Configuration.EnableHistorySync)
        {
            return HintApplyResult.Dropped("history sync is off on this server");
        }

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
            return HintApplyResult.Dropped("no local item at the mapped path yet. The next content sync or scan will pick it up");
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

        // A base says what this server and the origin last agreed on. When this server's value has since
        // come from a third server, the base no longer describes it, and a three way merge would read that
        // change as this server's own: a play taken from C would then beat a newer unmark from B. The edit
        // versions decide instead, as they do on first contact.
        var decideByVersion = !record.HasNegotiatedBase || ChangedByThirdServer(localKey, origin);
        if (decideByVersion)
        {
            DecideByVersion(record, hint, localKey);
        }

        if (!HistorySyncMergeService.HasChangesToSync(record))
        {
            // Both servers already hold the merged state, so this becomes the agreed base and the hint's
            // version is adopted as the version of the value here.
            var baseMoved = !record.HasNegotiatedBase
                || !PeerHistoryNegotiator.StatesMatch(PeerHistoryNegotiator.NegotiatedStateOf(record), PeerHistoryNegotiator.MergedStateOf(record));
            record.RecordNegotiatedBase(now);
            Store(record, now);
            _versions.Set(HintProtocol.IncomingVersion(hint, HintKind.History, localKey));
            if (baseMoved)
            {
                // The origin records the same base only when it hears of it, so a base that moved here is
                // confirmed there with an offer of the state both already hold.
                await ConfirmBaseWithOriginAsync(record, origin, client, cancellationToken).ConfigureAwait(false);
            }

            return HintApplyResult.Unchanged;
        }

        // A key that is not an administrator's cannot negotiate, since the origin's Server Sync
        // endpoints refuse it. The merged state is then written here only, the way one way history
        // always worked, and no base is recorded because the origin never agreed to anything.
        var oneWay = false;
        NegotiationStep settled;
        try
        {
            settled = await SettleWithOriginAsync(record, client, decideByVersion ? r => DecideByVersion(r, hint, localKey) : null, cancellationToken).ConfigureAwait(false);
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

        PeerHistoryNegotiator.ApplyLocalState(record, PeerHistoryNegotiator.MergedStateOf(record));
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
            // The origin already holds it through the negotiation above, unless the key is a standard
            // user's and nothing was negotiated, in which case the origin hears of it too.
            var version = new ObjectVersion { ServerId = _applicationHost.SystemId, Timestamp = now };
            _publisher.PublishHistory(localUserId, userMapping.LocalUserName, localItemId, record.LocalPath ?? string.Empty, version, excludePeerKey: oneWay ? null : origin.Key, itemType: hint.ItemType);
        }
        else
        {
            _versions.Set(HintProtocol.IncomingVersion(hint, HintKind.History, localKey));
        }

        _logger.LogInformation("Applied a history hint from '{Origin}' for {Item}: {Changes}", origin.DisplayName, record.ItemName, HistorySyncMergeService.GetChangeSummary(record));
        return HintApplyResult.AppliedTo(_libraryManager.GetItemById(localItemId));
    }

    // Whether this server's current value came from a server other than itself and the origin, by the
    // version it carries.
    private bool ChangedByThirdServer(string localKey, SourceServer origin)
    {
        var local = _versions.Get(HintKind.History, localKey);
        return local is not null
            && !string.Equals(local.ServerId, _applicationHost.SystemId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(local.ServerId, origin.ServerId, StringComparison.OrdinalIgnoreCase);
    }

    // Best effort: the hint is already settled here, so a refused key or an unreachable origin leaves
    // the origin's base where it was and the next exchange records it.
    private async Task ConfirmBaseWithOriginAsync(HistorySyncItem record, SourceServer origin, SourceServerClient client, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await OfferAsync(record, client, cancellationToken).ConfigureAwait(false);
            if (answer.Outcome is not (PeerHistoryOutcome.Applied or PeerHistoryOutcome.Unchanged))
            {
                _logger.LogDebug("'{Origin}' did not record the base for {Item}: {Outcome} {Reason}", origin.DisplayName, record.ItemName, answer.Outcome, answer.Reason);
            }
        }
        catch (PeerRefusedException)
        {
            // A standard user's key cannot negotiate, so there is no base on the origin to keep.
        }
    }

    /// <summary>
    /// The merge cannot be trusted to tell the newer change: the two servers have never agreed on this
    /// object, or this server's value has since come from a third server. When this server knows when
    /// its own value was last edited, the newer edit wins outright on origin versions, as the plan
    /// requires. Without a local version the merge stands, which keeps a play that predates versioning
    /// rather than risk losing it, and the base recorded after this apply makes every later merge three
    /// way.
    /// </summary>
    private void DecideByVersion(HistorySyncItem record, InboundHint hint, string localKey)
    {
        var localVersion = _versions.Get(HintKind.History, localKey);
        if (localVersion is null)
        {
            return;
        }

        var incoming = HintProtocol.IncomingVersion(hint, HintKind.History, localKey);
        var source = PeerHistoryNegotiator.SourceStateOf(record);
        var local = PeerHistoryNegotiator.LocalStateOf(record);

        switch (VersionDecider.Decide(localVersion, incoming, PeerHistoryNegotiator.StatesMatch(source, local)))
        {
            case VersionDecision.Apply:
                PeerHistoryNegotiator.ApplyMergedState(record, source);
                _logger.LogDebug("Deciding {Item} by version: the origin's edit is newer, taking it", record.ItemName);
                break;

            case VersionDecision.Keep:
                PeerHistoryNegotiator.ApplyMergedState(record, local);
                _logger.LogDebug("Deciding {Item} by version: this server's edit is newer, keeping it", record.ItemName);
                break;

            default:
                break;
        }
    }

    private async Task<NegotiationStep> SettleWithOriginAsync(HistorySyncItem record, SourceServerClient client, Action<HistorySyncItem>? remerge, CancellationToken cancellationToken)
    {
        var answer = await OfferAsync(record, client, cancellationToken).ConfigureAwait(false);
        var step = PeerHistoryNegotiator.ResolveOutcome(record, answer, allowRetry: true, remerge);
        if (step.Action == NegotiationAction.Retry)
        {
            answer = await OfferAsync(record, client, cancellationToken).ConfigureAwait(false);
            step = PeerHistoryNegotiator.ResolveOutcome(record, answer, allowRetry: false, remerge);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
        record.MarkApplied(now);
        _table.Upsert(record);
    }
}
