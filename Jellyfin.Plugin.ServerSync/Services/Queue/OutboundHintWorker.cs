using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Delivers outbound hints. Runs for the life of the server, not as a scheduled task, so a hint never
/// waits for the next scan and a slow download never delays it. Rows go to each peer in order, in
/// batches. A peer that answers 200 holds the hints and the rows wait for its completion report. A
/// peer that refuses the key or lacks the plugin is left alone for a while and the reason is kept for
/// the dashboard. Anything else is retried with backoff, forever. A row that was accepted but never
/// completed within the grace period is checked against the peer's queue and sent again when lost.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class OutboundHintWorker : IHostedService, IDisposable
{
    private const int BatchSize = 100;
    private static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StatusCheckSpacing = TimeSpan.FromMinutes(5);

    private readonly OutboundHintStore _outbound;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IServerApplicationHost _applicationHost;
    private readonly HintActivityLog _activity;
    private readonly HintSuppression _suppression;
    private readonly MediaBrowser.Controller.Library.ILibraryManager _libraryManager;
    private readonly ILogger<OutboundHintWorker> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);

    // One delivery pass at a time, whether the loop or the dashboard's Run starts it, so two passes never
    // send the same rows or race on their state.
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly ConcurrentDictionary<string, PeerDeliveryState> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastStatusCheck = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _stopping;
    private Task? _loop;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboundHintWorker"/> class.
    /// </summary>
    /// <param name="outbound">The outbound store.</param>
    /// <param name="configManager">Plugin configuration.</param>
    /// <param name="clientFactory">Client factory for peers.</param>
    /// <param name="applicationHost">The server host, for this server's id.</param>
    /// <param name="activity">Writes pauses and rejections to Jellyfin's activity log.</param>
    /// <param name="logger">Logger.</param>
    public OutboundHintWorker(
        OutboundHintStore outbound,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        IServerApplicationHost applicationHost,
        HintActivityLog activity,
        HintSuppression suppression,
        MediaBrowser.Controller.Library.ILibraryManager libraryManager,
        ILogger<OutboundHintWorker> logger)
    {
        _suppression = suppression;
        _libraryManager = libraryManager;
        _outbound = outbound;
        _configManager = configManager;
        _clientFactory = clientFactory;
        _applicationHost = applicationHost;
        _activity = activity;
        _logger = logger;
    }

    /// <summary>Gets the delivery state of every peer that has been tried, by entry key.</summary>
    public IReadOnlyDictionary<string, PeerDeliveryState> PeerStates => _peers;

    /// <summary>
    /// Whether a peer applies a kind of hint, as last read from its capabilities. A peer not yet asked,
    /// or one too old to say, is taken to accept everything, and it declines what it does not want.
    /// </summary>
    /// <param name="peerKey">The peer's entry key.</param>
    /// <param name="kind">The kind.</param>
    /// <returns><c>true</c> unless the peer said its module for that kind is off.</returns>
    public bool PeerAccepts(string peerKey, HintKind kind)
        => !_peers.TryGetValue(peerKey, out var state) || state.Interest is null || state.Interest.Kinds.Contains(kind);

    public bool PeerWants(string peerKey, HintKind kind, string? itemPath, Guid userId, string key)
    {
        var interest = _peers.TryGetValue(peerKey, out var state) ? state.Interest : null;
        return (interest is null || interest.Wants(kind, itemPath, userId))
            && !_suppression.IsHeld(peerKey, kind, key, interest?.Revision, DateTime.UtcNow);
    }

    public int Complete(SourceServer peer, IList<CompletedHint> completed)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(completed);
        var declined = completed.Where(c => HintSuppression.Suppresses(c.Code)).ToList();
        if (declined.Count > 0)
        {
            var revision = _peers.TryGetValue(peer.Key, out var state) ? state.Interest?.Revision : null;
            var rows = _outbound.GetByHintIds(peer.Key, declined.Select(c => c.HintId));
            var now = DateTime.UtcNow;
            foreach (var done in declined)
            {
                if (rows.TryGetValue(done.HintId, out var row))
                {
                    _suppression.Note(peer.Key, row.Kind, row.Key, done.Code, revision, now);
                }
            }

            _logger.LogDebug("'{Peer}' will not use {Count} hint(s): {Codes}", peer.DisplayName, declined.Count, string.Join(", ", declined.GroupBy(c => c.Code).Select(g => $"{g.Key} {g.Count()}")));
        }

        return _outbound.Complete(peer.Key, completed);
    }

    /// <summary>Asks the worker to deliver now rather than at its next idle tick.</summary>
    public void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already awake.
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping is not null)
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on stop.
            }
        }
    }

    /// <summary>One delivery pass over every peer. Public so the dashboard and tests can run it on demand.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="refreshCapabilities">Re-read what every peer accepts now, rather than only when the last answer is old.</param>
    /// <returns>A task.</returns>
    public async Task<bool> DeliverAsync(CancellationToken cancellationToken, bool refreshCapabilities = false)
    {
        await _pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DeliverPassAsync(refreshCapabilities, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pass.Release();
        }
    }

    private async Task<bool> DeliverPassAsync(bool refreshCapabilities, CancellationToken cancellationToken)
    {
        var config = _configManager.Configuration;

        // Every peer is asked at once, so one that does not answer costs the others nothing.
        await Task.WhenAll(config.GetPushServers().Select(peer =>
            RefreshInterestAsync(peer, _peers.GetOrAdd(peer.Key, _ => new PeerDeliveryState()), refreshCapabilities, cancellationToken))).ConfigureAwait(false);

        var deliveries = new List<Task<bool>>();
        foreach (var peerKey in _outbound.GetPeerKeys())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var peer = config.FindServer(peerKey);
            if (peer is null)
            {
                var removed = _outbound.DeleteForPeer(peerKey);
                _peers.TryRemove(peerKey, out _);
                _lastStatusCheck.TryRemove(peerKey, out _);
                _logger.LogInformation("Dropped {Count} queued hint(s) for a server that is no longer configured", removed);
                continue;
            }

            if (!peer.Pushes)
            {
                // Rows for a peer that is disabled or no longer in Push or Sync mode have nowhere to go.
                // The scheduled tasks cover the change if the peer is switched back on.
                var stale = _outbound.DeleteForPeer(peerKey);
                _peers.TryRemove(peerKey, out _);
                _logger.LogInformation("Dropped {Count} queued hint(s) for '{Peer}', which is disabled or no longer in Push or Sync mode", stale, peer.DisplayName);
                continue;
            }

            _outbound.TrimPending(peerKey);
            var state = _peers.GetOrAdd(peerKey, _ => new PeerDeliveryState());
            if (state.PausedUntil > DateTime.UtcNow || state.VersionBlocked)
            {
                continue;
            }

            deliveries.Add(DeliverToPeerSafelyAsync(peer, state, cancellationToken));
        }

        var more = await Task.WhenAll(deliveries).ConfigureAwait(false);
        return more.Any(m => m);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _wake.Dispose();
        _pass.Dispose();
        _stopping?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // A pass that filled a batch goes again straight away, so a backlog drains at the pace the peers
        // answer rather than one batch every idle wait.
        var more = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!more)
                {
                    await _wake.WaitAsync(IdleWait, cancellationToken).ConfigureAwait(false);
                }

                more = await DeliverAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbound hint worker pass failed");
            }
        }
    }

    private async Task<bool> DeliverToPeerSafelyAsync(SourceServer peer, PeerDeliveryState state, CancellationToken cancellationToken)
    {
        try
        {
            return await DeliverToPeerAsync(peer, state, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hint delivery to '{Peer}' failed unexpectedly", peer.DisplayName);
            return false;
        }
    }

    // Returns whether a full batch was sent, so the caller goes again at once.
    private async Task<bool> DeliverToPeerAsync(SourceServer peer, PeerDeliveryState state, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var due = DropUnwanted(peer, _outbound.GetDue(peer.Key, now, BatchSize));

        // Sent rows are only looked at when recovery is due, every few minutes, not on every pass.
        var sent = CheckDue(peer.Key, now) ? _outbound.GetSentBefore(peer.Key, now - HintProtocol.CompletionGrace) : Array.Empty<OutboundHint>();
        if (due.Count == 0 && sent.Count == 0)
        {
            return false;
        }

        SourceServerClient client;
        try
        {
            client = _clientFactory.Create(peer);
        }
        catch (ArgumentException ex)
        {
            // An address the private network rule refuses, or one that is not a URL. Nothing can be sent
            // until the entry is fixed, so the peer pauses with the reason rather than failing every pass.
            if (due.Count > 0)
            {
                await PauseAsync(peer, state, due, "its address cannot be used: " + ex.Message).ConfigureAwait(false);
            }

            return false;
        }

        using var owned = client;
        if (due.Count > 0)
        {
            var request = new QueueRequest { SenderServerId = _applicationHost.SystemId };
            foreach (var row in due)
            {
                request.Items.Add(row.ToHint(_applicationHost.SystemId));
            }

            int status;
            QueueResponse? response;
            string? body;
            try
            {
                (status, response, body) = await client.SendHintsAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                status = 0;
                response = null;
                body = ex.Message;
            }

            await RecordAsync(peer, state, due, status, response, body).ConfigureAwait(false);
        }

        if (sent.Count > 0)
        {
            await RecoverLostAsync(peer, client, sent, cancellationToken).ConfigureAwait(false);
        }

        return due.Count >= BatchSize && state.PausedUntil is null;
    }

    private List<OutboundHint> DropUnwanted(SourceServer peer, IList<OutboundHint> due)
    {
        var kept = new List<OutboundHint>(due.Count);
        var dropped = 0;
        foreach (var row in due)
        {
            var userId = Guid.TryParse(row.UserId, out var parsedUser) ? parsedUser : Guid.Empty;
            if (!Exists(row) || !PeerWants(peer.Key, row.Kind, row.ItemPath, userId, row.Key))
            {
                _outbound.DeleteSent(row);
                dropped++;
                continue;
            }

            kept.Add(row);
        }

        if (dropped > 0)
        {
            _logger.LogDebug("Dropped {Count} queued hint(s) for '{Peer}' whose object is gone here or that it does not want", dropped, peer.DisplayName);
        }

        return kept;
    }

    private bool Exists(OutboundHint row)
    {
        if (row.Kind == HintKind.Playlists && HintProtocol.IsPlaylistDeletion(row.ItemType))
        {
            return true;
        }

        if (row.Kind == HintKind.People)
        {
            return _libraryManager.GetPerson(row.Key) is not null;
        }

        return !Guid.TryParse(row.ItemId, out var itemId) || _libraryManager.GetItemById(itemId) is not null;
    }

    private async Task RecordAsync(SourceServer peer, PeerDeliveryState state, List<OutboundHint> due, int status, QueueResponse? response, string? body)
    {
        var now = DateTime.UtcNow;
        state.LastAttempt = now;

        switch (HintDelivery.Classify(status))
        {
            case DeliveryOutcome.Accepted:
                state.PausedUntil = null;
                state.Reason = null;
                var accepted = new List<OutboundHint>();
                var declined = new Dictionary<HintCode, int>();
                var revision = state.Interest?.Revision;
                var answers = (response?.Items ?? new List<QueueResult>()).GroupBy(r => r.HintId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                foreach (var row in due)
                {
                    if (!answers.TryGetValue(row.HintId, out var answer) || answer.Code == HintCode.Queued)
                    {
                        accepted.Add(row);
                        continue;
                    }

                    _outbound.DeleteSent(row);
                    _suppression.Note(peer.Key, row.Kind, row.Key, answer.Code, revision, now);

                    declined[answer.Code] = declined.GetValueOrDefault(answer.Code) + 1;
                    _logger.LogDebug("'{Peer}' answered {Code} for hint {Hint}: {Reason}", peer.DisplayName, answer.Code, row.HintId, answer.Reason);
                }

                _outbound.MarkSent(accepted, now);
                if (declined.Count > 0)
                {
                    _logger.LogInformation("Sent {Count} hint(s) to '{Peer}'. It closed {Declined} more on receipt: {Codes}", accepted.Count, peer.DisplayName, declined.Values.Sum(), string.Join(", ", declined.Select(d => $"{d.Key} {d.Value}")));
                }
                else
                {
                    _logger.LogInformation("Sent {Count} hint(s) to '{Peer}'", accepted.Count, peer.DisplayName);
                }
                break;

            case DeliveryOutcome.Malformed:
                // The receiver declines a bad hint on its own and answers 400 only for the request as a
                // whole, so this is a problem with the batch, written once rather than once per row.
                foreach (var row in due)
                {
                    _outbound.MarkFailed(row, "peer rejected the request as malformed: " + Trim(body));
                }

                await _activity.RejectedBatchAsync(peer.DisplayName, due.ToList(), Trim(body)).ConfigureAwait(false);

                _logger.LogError("'{Peer}' rejected {Count} hint(s) as malformed: {Body}", peer.DisplayName, due.Count, Trim(body));
                break;

            case DeliveryOutcome.PausePeer:
                await PauseAsync(peer, state, due, HintDelivery.PauseReason(status, Trim(body))).ConfigureAwait(false);
                break;

            default:
                var error = status == 0 ? "no answer: " + Trim(body) : $"peer answered {status}: {Trim(body)}";
                var config = _configManager.Configuration;
                var retry = GiveUp(peer, due, config.HintMaxRetries, error);

                // Rows that failed as often share a backoff, so each group is put off in one transaction.
                foreach (var group in retry.GroupBy(r => r.Attempts))
                {
                    _outbound.Defer(group, now + HintProtocol.OutboundDelay(config, group.Key + 1), error);
                }

                state.Reason = error;
                _logger.LogWarning("Could not deliver {Count} hint(s) to '{Peer}', will retry: {Error}", due.Count, peer.DisplayName, error);
                break;
        }
    }

    // Marks failed the rows that have used up their attempts and returns the rest to be tried again.
    private List<OutboundHint> GiveUp(SourceServer peer, IList<OutboundHint> due, int maxRetries, string error)
    {
        var retry = new List<OutboundHint>();
        var failed = 0;
        foreach (var row in due)
        {
            if (HintProtocol.OutOfRetries(maxRetries, row.Attempts + 1))
            {
                _outbound.MarkFailed(row, $"gave up after {row.Attempts + 1} attempts: {error}");
                failed++;
            }
            else
            {
                retry.Add(row);
            }
        }

        if (failed > 0)
        {
            _logger.LogWarning("Gave up on {Count} hint(s) to '{Peer}' after {Max} attempts: {Error}", failed, peer.DisplayName, maxRetries, error);
        }

        return retry;
    }

    private async Task PauseAsync(SourceServer peer, PeerDeliveryState state, IList<OutboundHint> due, string reason)
    {
        var config = _configManager.Configuration;
        var until = DateTime.UtcNow + HintProtocol.PeerPause(config);
        var alreadyPaused = state.PausedUntil.HasValue && string.Equals(state.Reason, reason, StringComparison.Ordinal);
        state.PausedUntil = until;
        state.Reason = reason;
        _outbound.Defer(GiveUp(peer, due, config.HintRefusedMaxRetries, reason), until, reason);
        _logger.LogError("Hints to '{Peer}' are paused until {Until:u}: {Reason}", peer.DisplayName, until, reason);
        if (!alreadyPaused)
        {
            await _activity.PausedAsync(peer.DisplayName, reason, due.ToList(), until).ConfigureAwait(false);
        }
    }

    private async Task RefreshInterestAsync(SourceServer peer, PeerDeliveryState state, bool force, CancellationToken cancellationToken)
    {
        if (!force && (state.PausedUntil > DateTime.UtcNow || DateTime.UtcNow - state.InterestReadAt < HintProtocol.CapabilityRefresh))
        {
            return;
        }

        state.InterestReadAt = DateTime.UtcNow;
        try
        {
            using var client = _clientFactory.Create(peer);
            var capabilities = await client.GetPeerCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            if (capabilities is not null && !HintProtocol.Compatible(capabilities, _configManager.PluginVersion))
            {
                var reason = HintProtocol.ProtocolMismatch(capabilities, _configManager.PluginVersion);
                if (!state.VersionBlocked)
                {
                    _logger.LogWarning("'{Peer}': {Reason}, so changes for it wait here until the two are compatible", peer.DisplayName, reason);
                }

                state.VersionBlocked = true;
                state.PausedUntil = DateTime.UtcNow + HintProtocol.CapabilityRefresh;
                state.Reason = reason;
                return;
            }

            if (capabilities is not null && state.VersionBlocked)
            {
                state.VersionBlocked = false;
                state.PausedUntil = null;
                state.Reason = null;
                _logger.LogInformation("'{Peer}' now runs the same Server Sync version, so waiting changes go out again", peer.DisplayName);
            }

            var interest = await client.GetPeerInterestAsync(_applicationHost.SystemId, cancellationToken).ConfigureAwait(false);
            if (interest is null)
            {
                return;
            }

            if (state.Interest is null || !string.Equals(state.Interest.Revision, interest.Revision, StringComparison.Ordinal))
            {
                var cleared = _suppression.ClearPeer(peer.Key);
                _logger.LogInformation(
                    "'{Peer}' wants {Kinds} from {Libraries} librar(ies) and {Users} user(s){Cleared}",
                    peer.DisplayName,
                    interest.Kinds.Count == 0 ? "no hints" : string.Join(", ", interest.Kinds),
                    interest.Libraries.Count,
                    interest.Users.Count,
                    cleared > 0 ? $". Its settings changed, so {cleared} held back hint(s) may go again" : string.Empty);
            }

            state.Interest = interest;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (System.Net.Http.HttpRequestException ex) when ((int?)ex.StatusCode is 401 or 403 or 404 or 409 or 426 or 428)
        {
            state.Interest = null;
            _logger.LogDebug("'{Peer}' refused to say what it wants ({Status}). Sending everything until it answers, so the refusal surfaces as a pause", peer.DisplayName, (int?)ex.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read what '{Peer}' wants. Keeping the last answer", peer.DisplayName);
        }
    }

    private bool CheckDue(string peerKey, DateTime now)
    {
        if (_lastStatusCheck.TryGetValue(peerKey, out var last) && now - last < StatusCheckSpacing)
        {
            return false;
        }

        _lastStatusCheck[peerKey] = now;
        return true;
    }

    private async Task RecoverLostAsync(SourceServer peer, SourceServerClient client, IList<OutboundHint> sent, CancellationToken cancellationToken)
    {
        QueueStatusResponse? status;
        try
        {
            status = await client.GetPeerQueueStatusAsync(_applicationHost.SystemId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the hint queue on '{Peer}'", peer.DisplayName);
            return;
        }

        if (status is null)
        {
            return;
        }

        var byId = sent.ToDictionary(r => r.HintId, StringComparer.Ordinal);
        var held = new HashSet<string>(status.Inbound.Select(i => i.HintId), StringComparer.Ordinal);

        // A hint id is reused for every edit of the same object, so a completion only counts when the
        // peer finished at least the version the row now carries. An id alone says nothing about which
        // edit was finished, so a completion without a version never removes a row. Every peer that
        // speaks hints reports versions.
        var completed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var done in status.CompletedHints ?? new List<CompletedHint>())
        {
            if (byId.TryGetValue(done.HintId, out var row) && Utilities.UtcTime.AsUtc(done.VersionTimestamp) >= Utilities.UtcTime.AsUtc(row.VersionTimestamp))
            {
                completed.Add(done.HintId);
            }
        }

        var (finished, lostIds) = HintDelivery.Reconcile(sent.Select(r => r.HintId), held, completed);
        if (finished.Count > 0)
        {
            // The peer finished these but could not say so, which happens when it holds a standard
            // user's key for this server. Its status says it, so the rows are complete.
            var reported = (status.CompletedHints ?? new List<CompletedHint>()).GroupBy(c => c.HintId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            Complete(peer, finished.Select(id => new CompletedHint
            {
                HintId = id,
                VersionTimestamp = byId[id].VersionTimestamp,
                Code = reported.TryGetValue(id, out var done) ? done.Code : HintCode.Applied,
                Reason = reported.TryGetValue(id, out var doneReason) ? doneReason.Reason : null
            }).ToList());
            _logger.LogInformation("'{Peer}' finished {Count} hint(s) it could not report. Completed from its status", peer.DisplayName, finished.Count);
        }

        var lost = sent.Where(row => lostIds.Contains(row.HintId)).ToList();
        if (lost.Count > 0)
        {
            _outbound.Resend(lost.Select(row => row.Id), DateTime.UtcNow);
            _logger.LogWarning("'{Peer}' lost {Count} hint(s) before completing them, sending again", peer.DisplayName, lost.Count);
            await _activity.ResentAsync(peer.DisplayName, lost).ConfigureAwait(false);
        }
    }

    private static string Trim(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(no detail)";
        }

        var text = body.Trim();
        return text.Length > 200 ? text[..200] : text;
    }
}

/// <summary>What the dashboard shows about delivery to one peer.</summary>
public sealed class PeerDeliveryState
{
    /// <summary>Gets or sets when the last delivery was tried, in UTC.</summary>
    public DateTime? LastAttempt { get; set; }

    /// <summary>Gets or sets until when the peer is left alone, in UTC, or null when it is not paused.</summary>
    public DateTime? PausedUntil { get; set; }

    /// <summary>Gets or sets why the last delivery did not go through, or null after a success.</summary>
    public string? Reason { get; set; }

    public Models.Peer.PeerInterest? Interest { get; set; }

    public bool VersionBlocked { get; set; }

    /// <summary>Gets the kinds the peer applies, or null when not known.</summary>
    public HashSet<HintKind>? Accepts => Interest is null ? null : new HashSet<HintKind>(Interest.Kinds);

    public DateTime InterestReadAt { get; set; }
}
