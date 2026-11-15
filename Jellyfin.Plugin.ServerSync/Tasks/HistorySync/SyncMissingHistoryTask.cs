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
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.HistorySync;

/// <summary>
/// Applies queued <see cref="HistorySyncItem"/> rows by writing the merged play state to this server, then reads it back and errors the row with a precise reason when it did not land.
/// When a library negotiates, the merged state is offered to the source's Server Sync first, and the local write happens only once the source accepts it.
/// </summary>
public class SyncMissingHistoryTask
    : SyncQueueTaskBase<HistorySyncItem, (string SourceUserId, string SourceItemId)>
{
    private readonly LocalServerClient _localClient;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ApplyGuard? _guard;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncMissingHistoryTask"/> class.
    /// </summary>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="clientFactory">Builds the client for each source.</param>
    /// <param name="localClient">Calls this server's own API.</param>
    /// <param name="manager">Reads and writes the sync table.</param>
    /// <param name="applicationHost">Gives this server's id and name.</param>
    /// <param name="guard">Stops a change this server applies from echoing back, or null when none is kept.</param>
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

    /// <summary>
    /// Registers a scheduled history write with the guard, since it is a copy and not an edit made here.
    /// Without it the change observer would send a hint to every peer and record this server as the editor.
    /// </summary>
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

    /// <summary>
    /// The answers from the group offer, which ApplyAsync takes one row at a time.
    /// It is keyed by reference, because the same row instance flows from GetApplyGroups through PrepareGroupAsync to ApplyAsync.
    /// </summary>
    private readonly ConcurrentDictionary<HistorySyncItem, PeerHistoryResult> _peerAnswers = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Servers that cannot negotiate this run, by entry key, with the reason their rows will carry.
    /// </summary>
    private readonly Dictionary<string, string> _negotiationBlocked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Servers whose Server Sync refuses this server's key.
    /// History from them is applied one way, and no base is recorded.
    /// </summary>
    private readonly HashSet<string> _oneWay = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether any library negotiates, which decides whether servers are asked about it at all. Each
    /// row then negotiates only when its own library does.
    /// </summary>
    private bool Negotiating => ConfigManager.Configuration.NegotiatesAnyHistory();

    /// <summary>Says whether a row's own library negotiates history.</summary>
    private bool NegotiatesFor(HistorySyncItem record)
        => ConfigManager.Configuration.NegotiatesHistory(record.ServerKey, record.SourceLibraryId);

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
    /// Confirms each source runs a Server Sync that can negotiate history before any row is touched, so a missing plugin shows as one clear run failure and not a row of identical errors.
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

        // A server that cannot negotiate errors its rows, and the others go on.
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
            catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                _oneWay.Add(source.Key);
                Logger.LogWarning("{Task}: Server Sync on '{Server}' refused this server's key ({Reason}), so it cannot be asked to negotiate. History from it is applied one way. Add this server or its user to the whitelist there", Name, source.Name, FormatUtilities.SanitizeForLog(ex.Message, 300));
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _negotiationBlocked[source.Key] = $"could not reach the Server Sync plugin on '{source.Name}': {ex.Message}";
                continue;
            }

            if (capabilities is null)
            {
                _negotiationBlocked[source.Key] = $"'{source.Name}' does not run Server Sync. Install Server Sync there, or turn off Negotiate With Source Server";
                continue;
            }

            if (!HintProtocol.Compatible(capabilities, ConfigManager.PluginVersion))
            {
                _negotiationBlocked[source.Key] = $"'{source.Name}': " + HintProtocol.ProtocolMismatch(capabilities, ConfigManager.PluginVersion);
                continue;
            }

            Logger.LogInformation("{Task}: negotiating history with Server Sync {Version} on '{Server}'", Name, FormatUtilities.SanitizeForLog(capabilities.PluginVersion), source.Name);
        }

        if (_negotiationBlocked.Count == Sources.Count)
        {
            FailPreflight(string.Join("; ", _negotiationBlocked.Values));
            return false;
        }

        foreach (var reason in _negotiationBlocked.Values)
        {
            Logger.LogError("{Task}: {Reason}", Name, FormatUtilities.SanitizeForLog(reason, 300));
        }

        return true;
    }

    /// <inheritdoc />
    protected override async Task ApplyAsync(HistorySyncItem record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var (localUserId, localItemId) = ParseLocalIds(record);

        if (NegotiatesFor(record))
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
    /// Offers a whole group to the source in one request and keeps each row's answer for <see cref="ApplyAsync(HistorySyncItem, CancellationToken)"/>, which saves a round trip for each row.
    /// A transport failure is recorded against every row in the group and surfaces as that row's error.
    /// </summary>
    /// <inheritdoc />
    protected override async Task PrepareGroupAsync(IList<HistorySyncItem> group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);

        if (group.Count == 0 || !group.Any(NegotiatesFor))
        {
            return;
        }

        // Groups never mix servers, so the first row's source serves the group.
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
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            foreach (var record in group)
            {
                _peerAnswers[record] = new PeerHistoryResult { Outcome = PeerHistoryOutcome.Failed, Reason = ex.Message };
            }
        }
    }

    /// <summary>
    /// Settles one row with the source before it is written locally, and merges and offers it once more when the source moved since the scan.
    /// A second refusal, a missing item, or a failed write on the source errors the row and skips the local write, so the two servers never diverge further.
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
            // Not prepared as part of a group, so this row is offered alone.
            answer = await OfferAloneAsync(record, cancellationToken).ConfigureAwait(false);
        }

        var step = PeerHistoryNegotiator.ResolveOutcome(record, answer, allowRetry: true);
        if (step.Action == NegotiationAction.Retry)
        {
            Logger.LogInformation("Source state for {ItemName} changed since refresh, merging again", FormatUtilities.SanitizeForLog(record.ItemName));
            answer = await OfferAloneAsync(record, cancellationToken).ConfigureAwait(false);
            step = PeerHistoryNegotiator.ResolveOutcome(record, answer, allowRetry: false);
        }

        if (step.Action == NegotiationAction.Fail)
        {
            throw new InvalidOperationException(step.Reason ?? "negotiation failed");
        }
    }

    /// <summary>Offers one row to its source on its own.</summary>
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

    /// <summary>Builds the offer for one row.</summary>
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
    /// Splits the rows into batches no larger than the peer route accepts when negotiating, each for one server.
    /// Without negotiation the base grouping is kept.
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

        // Each batch is offered to the server its rows came from.
        foreach (var rows in items.GroupBy(i => i.ServerKey ?? string.Empty, StringComparer.OrdinalIgnoreCase).Select(byServer => byServer.ToList()))
        {
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

        var (ok, reason) = VerifyApplied(record, localUserId, localItemId);
        if (!ok)
        {
            throw new InvalidOperationException(reason ?? "verification failed");
        }

        Logger.LogInformation("Apply History verified for {ItemName}: {Changes}{Peer}",
            FormatUtilities.SanitizeForLog(record.ItemName),
            HistorySyncMergeService.GetChangeSummary(record),
            NegotiatesFor(record) ? " (source updated as well)" : string.Empty);

        return Task.CompletedTask;
    }

    /// <summary>Parses the row's local user and item ids, or throws with the reason the row carries.</summary>
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

    /// <summary>Reads the user data back and lists every merged value that did not land.</summary>
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

        // Jellyfin drops sub second precision, so dates are compared to the second.
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

    /// <summary>
    /// Copies the merged values into the row's local snapshot, since a history row has no values to mark synced.
    /// Until the next scan reads the real local state, the row then sees local equal to merged and is not queued again.
    /// </summary>
    /// <inheritdoc />
    protected override void OnApplySucceeded(HistorySyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        PeerHistoryNegotiator.ApplyLocalState(record, PeerHistoryNegotiator.MergedStateOf(record));

        // Both servers hold the merged state now, so it becomes the next merge's base.
        if (NegotiatesFor(record) && !_oneWay.Contains(record.ServerKey ?? string.Empty))
        {
            PeerHistoryNegotiator.ApplySourceState(record, PeerHistoryNegotiator.MergedStateOf(record));
            record.UpdateSourceStateBundle();
            record.RecordNegotiatedBase(DateTime.UtcNow);
        }

        record.MarkSynced();
    }

    /// <inheritdoc />
    protected override void RecordRunCompleted(PluginConfiguration config, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.LastHistorySyncTime = utcNow;
    }
}
