using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JPKribs.Jellyfin.Base;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Applies a history hint by merging the origin's live history with this server's, settling the result with the origin, and writing it here.
/// A merge that moved past what the origin had is a change of this server's own, so it is published to the other peers.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class HistoryHintHandler
{
    private readonly HistorySyncTableService _tableService;
    private readonly HistorySyncTableManager _table;
    private readonly LocalServerClient _localClient;
    private readonly ApplyGuard _guard;
    private readonly VersionStore _versions;
    private readonly ILibraryManager _libraryManager;
    private readonly LocalHintPublisher _publisher;
    private readonly IServerApplicationHost _applicationHost;
    private readonly HintTriage _triage;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ILogger<HistoryHintHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HistoryHintHandler"/> class.
    /// </summary>
    /// <param name="tableService">Builds history rows.</param>
    /// <param name="table">Holds the history rows and their agreed bases.</param>
    /// <param name="localClient">Reads and writes this server's user data.</param>
    /// <param name="guard">Keeps the write from raising a hint of its own.</param>
    /// <param name="versions">Holds the stamps the merge is decided on.</param>
    /// <param name="publisher">Publishes changes of this server's own to the other peers.</param>
    /// <param name="applicationHost">Gives this server's id.</param>
    /// <param name="configManager">Tells whether History is on.</param>
    /// <param name="triage">Finds the local item a hint waits for.</param>
    /// <param name="libraryManager">Finds the local item, for its title.</param>
    /// <param name="logger">Writes what was applied and how it was decided.</param>
    public HistoryHintHandler(
        HistorySyncTableService tableService,
        HistorySyncTableManager table,
        LocalServerClient localClient,
        ApplyGuard guard,
        VersionStore versions,
        LocalHintPublisher publisher,
        IServerApplicationHost applicationHost,
        IPluginConfigurationManager configManager,
        HintTriage triage,
        ILibraryManager libraryManager,
        ILogger<HistoryHintHandler> logger)
    {
        _configManager = configManager;
        _triage = triage;
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

    /// <summary>Applies one history hint from one origin.</summary>
    /// <param name="hint">The hint that names the user and item on the origin.</param>
    /// <param name="origin">The server entry of the origin, whose mappings apply.</param>
    /// <param name="client">Reads the history from the origin and negotiates with it.</param>
    /// <param name="cancellationToken">Cancels the read and the negotiation.</param>
    /// <returns>How the hint ended.</returns>
    public async Task<HintApplyResult> ApplyAsync(InboundHint hint, SourceServer origin, SourceServerClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hint);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(client);

        if (!_configManager.Configuration.EnableHistorySync)
        {
            return HintApplyResult.Dropped(HintCode.ModuleOff, "history sync is off on this server");
        }

        if (!HintProtocol.TryParseHistoryKey(hint.Key, out var originUserId, out var originItemId))
        {
            return HintApplyResult.Dropped(HintCode.Invalid, "the hint's key is not a user id and item id");
        }

        var userMapping = HintMapping.FindBySourceUser(origin, originUserId);
        if (userMapping is null)
        {
            return HintApplyResult.Dropped(HintCode.NotMapped, $"user '{hint.UserName ?? hint.UserId}' on '{origin.DisplayName}' is not mapped here");
        }

        var libraryMapping = HintMapping.FindBySourcePath(origin, hint.ItemPath);
        if (libraryMapping is null)
        {
            return HintApplyResult.Dropped(HintCode.NotMapped, $"path '{hint.ItemPath}' on '{origin.DisplayName}' is not in a mapped library here");
        }

        if (_triage.MissingLocal(origin, HintKind.History, hint.ItemPath, hint.Key) is { } waitFor)
        {
            return HintApplyResult.Park(waitFor, "no local item at the mapped path yet. It applies when the item arrives");
        }

        var page = await client.GetItemsWithUserDataByIdsAsync(originUserId, new Guid?[] { originItemId }, cancellationToken).ConfigureAwait(false);
        if (page is null)
        {
            return HintApplyResult.RetryLater($"could not read the item's history from '{origin.DisplayName}'");
        }

        var dto = page.Items?.FirstOrDefault();
        if (dto is null || !dto.Id.HasValue)
        {
            return HintApplyResult.Dropped(HintCode.SourceGone, $"the item no longer exists on '{origin.DisplayName}'");
        }

        libraryMapping = HintMapping.FindBySourcePath(origin, dto.Path);
        if (libraryMapping is null)
        {
            return HintApplyResult.Dropped(HintCode.NotMapped, $"'{origin.DisplayName}' has the item at '{dto.Path}', which is not in a library mapped here");
        }

        if (HintTriage.IsFiltered(libraryMapping, dto.Path))
        {
            return HintApplyResult.Dropped(HintCode.Filtered, $"'{dto.Path}' is left out by the library mapping's filter here");
        }

        var sourceItemId = dto.Id.Value.ToString("N", CultureInfo.InvariantCulture);
        var record = _tableService.BuildRecord(userMapping, libraryMapping, dto, sourceItemId, negotiateWithSource: true);
        if (record is null)
        {
            return _triage.MissingLocal(origin, HintKind.History, dto.Path, hint.Key) is { } missing
                ? HintApplyResult.Park(missing, "no local item at the mapped path yet. It applies when the item arrives")
                : HintApplyResult.Dropped(HintCode.NotMapped, $"the local item or user mapped from '{dto.Path}' could not be matched here");
        }

        if (!Guid.TryParse(record.LocalUserId, out var localUserId) || !Guid.TryParse(record.LocalItemId, out var localItemId))
        {
            return HintApplyResult.Dropped(HintCode.Invalid, "the local user or item id is not valid");
        }

        var localKey = HintProtocol.HistoryKey(localUserId, localItemId);
        var now = DateTime.UtcNow;

        record.ServerKey = origin.Key;
        var previous = _table.GetByKey((record.SourceUserId, record.SourceItemId));
        if (previous is not null)
        {
            if (previous.Status == SyncStatus.Ignored)
            {
                return HintApplyResult.Dropped(HintCode.Ignored, "the row is ignored on this server");
            }

            record.Id = previous.Id;
            record.CarryNegotiatedBaseFrom(previous);
            if (record.HasNegotiatedBase)
            {
                HistorySyncMergeService.MergeHistoryData(record);
            }
        }

        var itemType = HintProtocol.SplitHistoryItemType(hint.ItemType, out var parts);
        var taken = HistoryParts.None;
        var decideByVersion = !record.HasNegotiatedBase || ChangedByThirdServer(localKey, origin);

        // A base cannot describe a value taken from a third server, so stamps decide.
        if (decideByVersion)
        {
            taken = DecideByVersion(record, hint, localKey, parts);
        }

        // Both servers already hold the merged state, so it becomes the agreed base.
        if (!HistorySyncMergeService.HasChangesToSync(record))
        {
            var baseMoved = !record.HasNegotiatedBase
                || !PeerHistoryNegotiator.StatesMatch(PeerHistoryNegotiator.NegotiatedStateOf(record), PeerHistoryNegotiator.MergedStateOf(record));
            record.RecordNegotiatedBase(now);
            Store(record, now);
            _versions.Set(HintProtocol.IncomingVersion(hint, HintKind.History, localKey));

            // The origin records the same base only when it hears of it.
            if (baseMoved)
            {
                await ConfirmBaseWithOriginAsync(record, origin, client, cancellationToken).ConfigureAwait(false);
            }

            return HintApplyResult.Unchanged;
        }

        var oneWay = false;
        NegotiationStep settled;
        try
        {
            settled = await SettleWithOriginAsync(record, client, decideByVersion ? r => taken = DecideByVersion(r, hint, localKey, parts) : null, cancellationToken).ConfigureAwait(false);
        }
        // A refused API Key cannot negotiate, so the merge is written here only.
        catch (PeerRefusedException ex) when (ex.KeyRefused)
        {
            oneWay = true;
            settled = new NegotiationStep(NegotiationAction.Proceed, null);
            _logger.LogWarning("Server Sync on '{Origin}' refused this server's key, so history from it is applied one way. Add this server or its user to the whitelist there", FormatUtilities.SanitizeForLog(origin.DisplayName));
        }

        if (settled.Action == NegotiationAction.Fail)
        {
            return HintApplyResult.RetryLater(settled.Reason ?? "negotiation with the origin failed");
        }

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

        var incomingVersion = HintProtocol.IncomingVersion(hint, HintKind.History, localKey);
        foreach (var part in new[] { HistoryParts.Played, HistoryParts.Favorite })
        {
            // Each part taken from the origin carries the origin's stamp on its own.
            if (taken.HasFlag(part))
            {
                _versions.Set(new ObjectVersion { Kind = HintKind.History, Key = HintProtocol.HistoryPartKey(localKey, part), ServerId = incomingVersion.ServerId, Timestamp = incomingVersion.Timestamp });
            }
        }

        // This server added to the merge, so the other peers hear it from here.
        if (movedPastOrigin)
        {
            var version = new ObjectVersion { ServerId = _applicationHost.SystemId, Timestamp = now };
            _publisher.PublishHistory(localUserId, userMapping.LocalUserName, localItemId, record.LocalPath ?? string.Empty, version, excludePeerKey: oneWay ? null : origin.Key, itemType: HintProtocol.HistoryItemType(itemType, taken == HistoryParts.None ? parts : taken));
        }
        else
        {
            _versions.Set(HintProtocol.IncomingVersion(hint, HintKind.History, localKey));
        }

        _logger.LogDebug("Applied a history hint from '{Origin}' for {Item}: {Changes}", FormatUtilities.SanitizeForLog(origin.DisplayName), FormatUtilities.SanitizeForLog(record.ItemName), HistorySyncMergeService.GetChangeSummary(record));
        return HintApplyResult.AppliedTo(_libraryManager.GetItemById(localItemId));
    }

    /// <summary>Whether this server's value came from a server other than itself and the origin, by the stamp it carries.</summary>
    private bool ChangedByThirdServer(string localKey, SourceServer origin)
    {
        var local = _versions.Get(HintKind.History, localKey);
        return local is not null
            && !string.Equals(local.ServerId, _applicationHost.SystemId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(local.ServerId, origin.ServerId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Offers the origin the state both servers already hold, so it records the same base.
    /// The hint is already settled here, so a failure leaves the origin's base for the next exchange.
    /// </summary>
    private async Task ConfirmBaseWithOriginAsync(HistorySyncItem record, SourceServer origin, SourceServerClient client, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await OfferAsync(record, client, cancellationToken).ConfigureAwait(false);
            if (answer.Outcome is not (PeerHistoryOutcome.Applied or PeerHistoryOutcome.Unchanged))
            {
                _logger.LogDebug("'{Origin}' did not record the base for {Item}: {Outcome} {Reason}", FormatUtilities.SanitizeForLog(origin.DisplayName), FormatUtilities.SanitizeForLog(record.ItemName), answer.Outcome, FormatUtilities.SanitizeForLog(answer.Reason, 300));
            }
        }
        // A standard user's API Key cannot negotiate, so the origin keeps no base.
        catch (PeerRefusedException)
        {
        }
    }

    /// <summary>
    /// Lets the newer stamp win when the merge cannot tell the newer change, which is on first contact or after a value came from a third server.
    /// Without a local stamp the merge stands, which keeps a play no stamp records.
    /// </summary>
    private HistoryParts DecideByVersion(HistorySyncItem record, InboundHint hint, string localKey, HistoryParts parts)
    {
        var localVersion = _versions.Get(HintKind.History, localKey);
        if (localVersion is null)
        {
            return HistoryParts.None;
        }

        var incoming = HintProtocol.IncomingVersion(hint, HintKind.History, localKey);
        var source = PeerHistoryNegotiator.SourceStateOf(record);
        var local = PeerHistoryNegotiator.LocalStateOf(record);

        // Only the parts the origin changed are weighed, each against its own stamp.
        if (parts != HistoryParts.None)
        {
            var taken = HistoryParts.None;
            var merged = local;
            foreach (var part in new[] { HistoryParts.Played, HistoryParts.Favorite })
            {
                if (!parts.HasFlag(part))
                {
                    continue;
                }

                var mine = _versions.Get(HintKind.History, HintProtocol.HistoryPartKey(localKey, part)) ?? localVersion;
                if (VersionDecider.SameVersion(incoming, mine) || !VersionDecider.IsNewer(incoming, mine))
                {
                    continue;
                }

                taken |= part;
                if (part == HistoryParts.Favorite)
                {
                    merged.IsFavorite = source.IsFavorite;
                }
                else
                {
                    merged.Played = source.Played;
                    merged.PlayCount = source.PlayCount;
                    merged.PlaybackPositionTicks = source.PlaybackPositionTicks;
                    merged.LastPlayedDate = source.LastPlayedDate;
                }
            }

            PeerHistoryNegotiator.ApplyMergedState(record, merged);
            _logger.LogDebug("Deciding {Item} by version, part by part: taking {Taken} of {Parts} from the origin", FormatUtilities.SanitizeForLog(record.ItemName), taken, parts);
            return taken;
        }

        switch (VersionDecider.Decide(localVersion, incoming, PeerHistoryNegotiator.StatesMatch(source, local)))
        {
            case VersionDecision.Apply:
                PeerHistoryNegotiator.ApplyMergedState(record, source);
                _logger.LogDebug("Deciding {Item} by version: the origin's edit is newer, taking it", FormatUtilities.SanitizeForLog(record.ItemName));
                break;

            case VersionDecision.Keep:
                PeerHistoryNegotiator.ApplyMergedState(record, local);
                _logger.LogDebug("Deciding {Item} by version: this server's edit is newer, keeping it", FormatUtilities.SanitizeForLog(record.ItemName));
                break;
        }

        return HistoryParts.None;
    }

    /// <summary>Offers the merged state to the origin, and once more when its live state had moved.</summary>
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

    /// <summary>Sends one offer to the origin and returns its answer, or a failed result when it could not be reached.</summary>
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
        // A refused API Key and a cancelled apply are the caller's to handle.
        catch (Exception ex) when (ex is not PeerRefusedException { KeyRefused: true } && (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            return new PeerHistoryResult { Outcome = PeerHistoryOutcome.Failed, Reason = ex.Message };
        }
    }

    /// <summary>Marks a row applied and stores it.</summary>
    private void Store(HistorySyncItem record, DateTime now)
    {
        record.MarkApplied(now);
        _table.Upsert(record);
    }
}
