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
    private readonly ILogger<OutboundHintWorker> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);
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
        ILogger<OutboundHintWorker> logger)
    {
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
        => !_peers.TryGetValue(peerKey, out var state) || state.Accepts is null || state.Accepts.Contains(kind);

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
    public async Task DeliverAsync(CancellationToken cancellationToken, bool refreshCapabilities = false)
    {
        var config = _configManager.Configuration;
        foreach (var peer in config.GetPushServers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshAcceptsAsync(peer, _peers.GetOrAdd(peer.Key, _ => new PeerDeliveryState()), refreshCapabilities, cancellationToken).ConfigureAwait(false);
        }

        foreach (var peerKey in _outbound.GetPeerKeys())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var peer = config.FindServer(peerKey);
            if (peer is null)
            {
                var removed = _outbound.DeleteForPeer(peerKey);
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

            var state = _peers.GetOrAdd(peerKey, _ => new PeerDeliveryState());
            if (state.PausedUntil > DateTime.UtcNow)
            {
                continue;
            }

            try
            {
                await DeliverToPeerAsync(peer, state, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hint delivery to '{Peer}' failed unexpectedly", peer.DisplayName);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _wake.Dispose();
        _stopping?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(IdleWait, cancellationToken).ConfigureAwait(false);
                await DeliverAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbound hint worker pass failed");
            }
        }
    }

    private async Task DeliverToPeerAsync(SourceServer peer, PeerDeliveryState state, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var due = _outbound.GetDue(peer.Key, now, BatchSize);
        var sent = _outbound.GetSentBefore(peer.Key, now - HintProtocol.CompletionGrace);
        if (due.Count == 0 && sent.Count == 0)
        {
            return;
        }

        using var client = _clientFactory.Create(peer);

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
            catch (OperationCanceledException)
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

        if (sent.Count > 0 && CheckDue(peer.Key, now))
        {
            await RecoverLostAsync(peer, client, sent, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RecordAsync(SourceServer peer, PeerDeliveryState state, IList<OutboundHint> due, int status, QueueResponse? response, string? body)
    {
        var now = DateTime.UtcNow;
        state.LastAttempt = now;

        switch (HintDelivery.Classify(status))
        {
            case DeliveryOutcome.Accepted:
                state.PausedUntil = null;
                state.Reason = null;
                var accepted = new List<long>();
                foreach (var row in due)
                {
                    var answer = response?.Items.FirstOrDefault(r => string.Equals(r.HintId, row.HintId, StringComparison.Ordinal));
                    if (answer is { Accepted: false })
                    {
                        // The peer has no mapping for it. That is the peer's business, so the hint is done.
                        _outbound.Delete(row.Id);
                        _logger.LogDebug("'{Peer}' declined hint {Hint}: {Reason}", peer.DisplayName, row.HintId, answer.Reason);
                    }
                    else
                    {
                        accepted.Add(row.Id);
                    }
                }

                _outbound.MarkSent(accepted, now);
                _logger.LogInformation("Sent {Count} hint(s) to '{Peer}'", accepted.Count, peer.DisplayName);
                break;

            case DeliveryOutcome.Malformed:
                foreach (var row in due)
                {
                    _outbound.MarkFailed(row.Id, "peer rejected the hint as malformed: " + Trim(body));
                    await _activity.RejectedAsync(row, peer.DisplayName, Trim(body)).ConfigureAwait(false);
                }

                _logger.LogError("'{Peer}' rejected {Count} hint(s) as malformed: {Body}", peer.DisplayName, due.Count, Trim(body));
                break;

            case DeliveryOutcome.PausePeer:
                await PauseAsync(peer, state, due, HintDelivery.PauseReason(status, Trim(body))).ConfigureAwait(false);
                break;

            default:
                var error = status == 0 ? "no answer: " + Trim(body) : $"peer answered {status}: {Trim(body)}";
                foreach (var row in due)
                {
                    _outbound.Defer(new[] { row.Id }, now + HintProtocol.NextDelay(row.Attempts + 1), error);
                }

                state.Reason = error;
                _logger.LogWarning("Could not deliver {Count} hint(s) to '{Peer}', will retry: {Error}", due.Count, peer.DisplayName, error);
                break;
        }
    }

    private async Task PauseAsync(SourceServer peer, PeerDeliveryState state, IList<OutboundHint> due, string reason)
    {
        var until = DateTime.UtcNow + HintProtocol.PeerPause;
        var alreadyPaused = state.PausedUntil.HasValue && string.Equals(state.Reason, reason, StringComparison.Ordinal);
        state.PausedUntil = until;
        state.Reason = reason;
        _outbound.Defer(due.Select(r => r.Id), until, reason);
        _logger.LogError("Hints to '{Peer}' are paused until {Until:u}: {Reason}", peer.DisplayName, until, reason);
        if (!alreadyPaused)
        {
            await _activity.PausedAsync(peer.DisplayName, reason).ConfigureAwait(false);
        }
    }

    // Reads the peer's capabilities every few minutes so the publisher skips kinds the peer has off. A
    // failed read keeps the last answer; a peer that never answered is sent everything.
    private async Task RefreshAcceptsAsync(SourceServer peer, PeerDeliveryState state, bool force, CancellationToken cancellationToken)
    {
        if (!force && (state.PausedUntil > DateTime.UtcNow || DateTime.UtcNow - state.AcceptsReadAt < HintProtocol.CapabilityRefresh))
        {
            return;
        }

        state.AcceptsReadAt = DateTime.UtcNow;
        try
        {
            using var client = _clientFactory.Create(peer);
            var capabilities = await client.GetPeerCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            if (capabilities?.Accepts is null)
            {
                state.Accepts = null;
                return;
            }

            var accepts = new HashSet<HintKind>();
            foreach (var name in capabilities.Accepts)
            {
                if (Enum.TryParse<HintKind>(name, ignoreCase: true, out var kind))
                {
                    accepts.Add(kind);
                }
            }

            if (state.Accepts is null || !state.Accepts.SetEquals(accepts))
            {
                _logger.LogInformation("'{Peer}' accepts {Kinds}", peer.DisplayName, accepts.Count == 0 ? "no hints" : string.Join(", ", accepts));
            }

            state.Accepts = accepts;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            // The key is refused, so nothing is known about the peer any more. Everything is sent so the
            // refusal surfaces as a pause with its reason rather than a silent skip.
            state.Accepts = null;
            _logger.LogDebug("'{Peer}' refused the key when asked what it accepts; sending everything until it answers", peer.DisplayName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read what '{Peer}' accepts; keeping the last answer", peer.DisplayName);
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
            status = await client.GetPeerQueueStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
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
        // peer finished at least the version the row now carries. A peer that reports no versions is
        // taken at its word on the ids alone.
        var completed = new HashSet<string>(StringComparer.Ordinal);
        if (status.CompletedHints is { Count: > 0 })
        {
            foreach (var done in status.CompletedHints)
            {
                if (byId.TryGetValue(done.HintId, out var row) && done.VersionTimestamp.ToUniversalTime() >= row.VersionTimestamp.ToUniversalTime())
                {
                    completed.Add(done.HintId);
                }
            }
        }
        else
        {
            completed.UnionWith(status.Completed ?? new List<string>());
        }

        var (finished, lostIds) = HintDelivery.Reconcile(sent.Select(r => r.HintId), held, completed);
        if (finished.Count > 0)
        {
            // The peer finished these but could not say so, which happens when it holds a standard
            // user's key for this server. Its status says it, so the rows are complete.
            _outbound.Complete(peer.Key, finished.Select(id => new CompletedHint { HintId = id, VersionTimestamp = byId[id].VersionTimestamp }));
            _logger.LogInformation("'{Peer}' finished {Count} hint(s) it could not report; completed from its status", peer.DisplayName, finished.Count);
        }

        var lost = sent.Where(row => lostIds.Contains(row.HintId)).Select(row => row.Id).ToList();
        if (lost.Count > 0)
        {
            _outbound.Resend(lost, DateTime.UtcNow);
            _logger.LogWarning("'{Peer}' lost {Count} hint(s) before completing them, sending again", peer.DisplayName, lost.Count);
            await _activity.ResentAsync(peer.DisplayName, lost.Count).ConfigureAwait(false);
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

    /// <summary>Gets or sets the kinds the peer applies, or null when not known.</summary>
    public HashSet<HintKind>? Accepts { get; set; }

    /// <summary>Gets or sets when the peer's capabilities were last read, in UTC.</summary>
    public DateTime AcceptsReadAt { get; set; }
}
