using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JPKribs.Jellyfin.Base;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Delivers hints to each peer in batches for the life of the server, so a hint never waits for the next scan.
/// A peer that cannot take hints is paused with the reason, anything else is retried with backoff, and a lost hint is sent again.
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
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<OutboundHintWorker> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Lets one delivery pass run at a time, so two passes never send the same rows.</summary>
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly ConcurrentDictionary<string, PeerDeliveryState> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastStatusCheck = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _stopping;
    private Task? _loop;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboundHintWorker"/> class.
    /// </summary>
    /// <param name="outbound">Holds the hints waiting to be sent.</param>
    /// <param name="configManager">Lists the server entries and the retry settings.</param>
    /// <param name="clientFactory">Builds the client that sends to each peer.</param>
    /// <param name="applicationHost">Gives this server's id.</param>
    /// <param name="activity">Writes pauses and rejections to the Activity page.</param>
    /// <param name="suppression">Holds the objects peers declined, which are not sent to them again.</param>
    /// <param name="libraryManager">Tells whether a row's object still exists here.</param>
    /// <param name="logger">Writes what each delivery did.</param>
    public OutboundHintWorker(
        OutboundHintStore outbound,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        IServerApplicationHost applicationHost,
        HintActivityLog activity,
        HintSuppression suppression,
        ILibraryManager libraryManager,
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
    /// Whether a peer applies a kind of hint, as last read from it.
    /// A peer not yet asked is taken to accept everything, and it declines what it does not want.
    /// </summary>
    /// <param name="peerKey">The peer's entry key.</param>
    /// <param name="kind">The sync type of the hint.</param>
    /// <returns><c>true</c> unless the peer said that sync type is off.</returns>
    public bool PeerAccepts(string peerKey, HintKind kind)
        => !_peers.TryGetValue(peerKey, out var state) || state.Interest is null || state.Interest.Kinds.Contains(kind);

    /// <summary>
    /// Whether a peer wants a hint for one object, which means it applies the kind, maps the object, and has not declined it.
    /// </summary>
    /// <param name="peerKey">The peer's entry key.</param>
    /// <param name="kind">The sync type of the hint.</param>
    /// <param name="itemPath">The item's local path, when the kind concerns an item.</param>
    /// <param name="userId">The local user, when the kind concerns a user.</param>
    /// <param name="key">This server's key for the object.</param>
    /// <returns><c>true</c> when the hint should be sent.</returns>
    public bool PeerWants(string peerKey, HintKind kind, string? itemPath, Guid userId, string key)
    {
        var interest = _peers.TryGetValue(peerKey, out var state) ? state.Interest : null;
        return (interest is null || interest.Wants(kind, itemPath, userId))
            && !_suppression.IsHeld(peerKey, kind, key, interest?.Revision, DateTime.UtcNow);
    }

    /// <summary>
    /// Removes the rows a peer reports done, and holds the ones it declined so they are not sent to it again.
    /// </summary>
    /// <param name="peer">The server entry of the peer that reports them.</param>
    /// <param name="completed">The peer's report.</param>
    /// <returns>How many rows were removed.</returns>
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

            _logger.LogDebug("'{Peer}' will not use {Count} hint(s): {Codes}", FormatUtilities.SanitizeForLog(peer.DisplayName), declined.Count, string.Join(", ", declined.GroupBy(c => c.Code).Select(g => $"{g.Key} {g.Count()}")));
        }

        return _outbound.Complete(peer.Key, completed);
    }

    /// <summary>Asks the worker to deliver at once and not at its next idle tick.</summary>
    public void Wake()
    {
        try
        {
            _wake.Release();
        }
        // A full gate means the worker is already awake.
        catch (SemaphoreFullException)
        {
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
            // The loop ends by cancellation when the service stops.
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Lifts every pause for the operator's retry, so the next pass tries each paused server at once.
    /// A server on an incompatible protocol stays paused.
    /// </summary>
    public void LiftPauses()
    {
        foreach (var state in _peers.Values)
        {
            if (!state.VersionBlocked)
            {
                state.PausedUntil = null;
            }
        }
    }

    /// <summary>Runs one delivery pass over every peer, one pass at a time.</summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <param name="refreshCapabilities"><c>true</c> to read what every peer accepts again, even when the last answer is fresh.</param>
    /// <returns><c>true</c> when a peer was sent a full batch and more may be due.</returns>
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

    /// <summary>Reads what each peer wants, drops rows that have nowhere to go, and delivers to every peer that is not paused.</summary>
    private async Task<bool> DeliverPassAsync(bool refreshCapabilities, CancellationToken cancellationToken)
    {
        var config = _configManager.Configuration;

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

            // Rows for a peer that does not take changes have nowhere to go.
            if (!peer.Pushes)
            {
                var stale = _outbound.DeleteForPeer(peerKey);
                _peers.TryRemove(peerKey, out _);
                _logger.LogInformation("Dropped {Count} queued hint(s) for '{Peer}', which is disabled or no longer set to Push or Sync", stale, FormatUtilities.SanitizeForLog(peer.DisplayName));
                continue;
            }

            _outbound.TrimPending(peerKey);
            var state = _peers.GetOrAdd(peerKey, _ => new PeerDeliveryState());

            // A pause is for the entry as it was, so a changed one retries.
            if (state.PausedUntil.HasValue && !state.VersionBlocked && state.PausedFor is not null
                && !string.Equals(state.PausedFor, EntrySignature(peer), StringComparison.Ordinal))
            {
                state.PausedUntil = null;
                state.PausedFor = null;
                _outbound.BringForward(peerKey, DateTime.UtcNow);
            }

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

    /// <summary>Delivers until stopped, going again at once after a full batch so a backlog drains at the pace the peers answer.</summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
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
            // The loop must outlive a failed pass, which then waits like an idle one.
            catch (Exception ex)
            {
                more = false;
                _logger.LogError(ex, "Outbound hint worker pass failed");
            }
        }
    }

    /// <summary>Delivers to one peer, so a failure with it never stops delivery to the others.</summary>
    private async Task<bool> DeliverToPeerSafelyAsync(SourceServer peer, PeerDeliveryState state, CancellationToken cancellationToken)
    {
        try
        {
            return await DeliverToPeerAsync(peer, state, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Hint delivery to '{Peer}' failed unexpectedly", FormatUtilities.SanitizeForLog(peer.DisplayName));
            return false;
        }
    }

    /// <summary>Sends one peer its due rows and recovers its lost ones, and says whether a full batch was sent.</summary>
    private async Task<bool> DeliverToPeerAsync(SourceServer peer, PeerDeliveryState state, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var due = DropUnwanted(peer, _outbound.GetDue(peer.Key, now, BatchSize));

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
        // Nothing can be sent to an address the factory refuses, so the peer pauses.
        catch (ArgumentException ex)
        {
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
            // A peer that cannot be reached counts as no answer, so its rows retry.
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
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

    /// <summary>Removes the due rows whose object is gone here or that the peer does not want, and returns the rest.</summary>
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
            _logger.LogDebug("Dropped {Count} queued hint(s) for '{Peer}' whose object is gone here or that it does not want", dropped, FormatUtilities.SanitizeForLog(peer.DisplayName));
        }

        return kept;
    }

    /// <summary>Whether the object a row names still exists here, where a playlist deletion and a file's removal always count, since their object is gone by design.</summary>
    private bool Exists(OutboundHint row)
    {
        if ((row.Kind == HintKind.Playlists && HintProtocol.IsPlaylistDeletion(row.ItemType))
            || (row.Kind == HintKind.Content && HintProtocol.IsContentRemoval(row.ItemType)))
        {
            return true;
        }

        if (row.Kind == HintKind.People)
        {
            return _libraryManager.GetPerson(row.Key) is not null;
        }

        return !Guid.TryParse(row.ItemId, out var itemId) || _libraryManager.GetItemById(itemId) is not null;
    }

    /// <summary>Marks a batch sent, failed, paused, or put off, by the peer's answer.</summary>
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
                    _logger.LogDebug("'{Peer}' answered {Code} for hint {Hint}: {Reason}", FormatUtilities.SanitizeForLog(peer.DisplayName), answer.Code, row.HintId, FormatUtilities.SanitizeForLog(answer.Reason, 300));
                }

                _outbound.MarkSent(accepted, now);
                if (declined.Count > 0)
                {
                    _logger.LogDebug("Sent {Count} hint(s) to '{Peer}'. It closed {Declined} more on receipt: {Codes}", accepted.Count, FormatUtilities.SanitizeForLog(peer.DisplayName), declined.Values.Sum(), string.Join(", ", declined.Select(d => $"{d.Key} {d.Value}")));
                }
                else
                {
                    _logger.LogDebug("Sent {Count} hint(s) to '{Peer}'", accepted.Count, FormatUtilities.SanitizeForLog(peer.DisplayName));
                }
                break;

            case DeliveryOutcome.Malformed:
                foreach (var row in due)
                {
                    _outbound.MarkFailed(row, "peer rejected the request as malformed: " + Trim(body));
                }

                await _activity.RejectedBatchAsync(peer.DisplayName, due.ToList()).ConfigureAwait(false);

                _logger.LogError("'{Peer}' rejected {Count} hint(s) as malformed: {Body}", FormatUtilities.SanitizeForLog(peer.DisplayName), due.Count, FormatUtilities.SanitizeForLog(Trim(body), 300));
                break;

            case DeliveryOutcome.PausePeer:
                await PauseAsync(peer, state, due, HintDelivery.PauseReason(status, Trim(body))).ConfigureAwait(false);
                break;

            default:
                var error = status == 0 ? "no answer: " + Trim(body) : $"peer answered {status}: {Trim(body)}";
                var config = _configManager.Configuration;
                var retry = GiveUp(peer, due, config.HintMaxRetries, error);

                foreach (var group in retry.GroupBy(r => r.Attempts))
                {
                    _outbound.Defer(group, now + HintProtocol.OutboundDelay(config, group.Key + 1), error);
                }

                state.Reason = error;
                _logger.LogDebug("Could not deliver {Count} hint(s) to '{Peer}', will retry: {Error}", due.Count, FormatUtilities.SanitizeForLog(peer.DisplayName), FormatUtilities.SanitizeForLog(error, 300));
                break;
        }
    }

    /// <summary>Marks failed the rows that have used up their attempts and returns the rest to be tried again.</summary>
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
            _logger.LogWarning("Gave up on {Count} hint(s) to '{Peer}' after {Max} attempts: {Error}", failed, FormatUtilities.SanitizeForLog(peer.DisplayName), maxRetries, FormatUtilities.SanitizeForLog(error, 300));
        }

        return retry;
    }

    /// <summary>Joins the settings of an entry that a pause depends on, so a changed entry can be told.</summary>
    private static string EntrySignature(SourceServer peer)
        => string.Join('|', peer.Url, peer.ApiKey, peer.AllowPrivateNetwork, peer.Mode, peer.IsEnabled);

    /// <summary>Pauses a peer with the reason, puts its due rows off until the pause ends, and writes the pause once.</summary>
    private async Task PauseAsync(SourceServer peer, PeerDeliveryState state, IList<OutboundHint> due, string reason)
    {
        var config = _configManager.Configuration;
        var until = DateTime.UtcNow + HintProtocol.PeerPause(config);
        var alreadyPaused = state.PausedUntil.HasValue && string.Equals(state.Reason, reason, StringComparison.Ordinal);
        state.PausedUntil = until;
        state.PausedFor = EntrySignature(peer);
        state.Reason = reason;
        _outbound.Defer(GiveUp(peer, due, config.HintRefusedMaxRetries, reason), until, reason);
        _logger.LogWarning("Hints to '{Peer}' are paused until {Until:u}: {Reason}", FormatUtilities.SanitizeForLog(peer.DisplayName), until, FormatUtilities.SanitizeForLog(reason, 300));
        if (!alreadyPaused)
        {
            await _activity.PausedAsync(peer.DisplayName, reason, due.ToList(), until).ConfigureAwait(false);
        }
    }

    /// <summary>Reads whether a peer is compatible and what it wants, keeping the last answer when it cannot be read.</summary>
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
                    _logger.LogWarning("'{Peer}': {Reason}, so changes for it wait here until the two are compatible", FormatUtilities.SanitizeForLog(peer.DisplayName), FormatUtilities.SanitizeForLog(reason, 300));
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
                _logger.LogInformation("'{Peer}' now runs the same Server Sync version, so waiting changes go out again", FormatUtilities.SanitizeForLog(peer.DisplayName));
            }

            var interest = await client.GetPeerInterestAsync(_applicationHost.SystemId, cancellationToken).ConfigureAwait(false);
            if (interest is null)
            {
                return;
            }

            if (state.Interest is null || !string.Equals(state.Interest.Revision, interest.Revision, StringComparison.Ordinal))
            {
                var cleared = _suppression.ClearPeer(peer.Key);
                _logger.LogDebug(
                    "'{Peer}' wants {Kinds} from {Libraries} librar(ies) and {Users} user(s){Cleared}",
                    FormatUtilities.SanitizeForLog(peer.DisplayName),
                    interest.Kinds.Count == 0 ? "no hints" : string.Join(", ", interest.Kinds),
                    interest.Libraries.Count,
                    interest.Users.Count,
                    cleared > 0 ? $". Its settings changed, so {cleared} held back hint(s) may go again" : string.Empty);
            }

            state.Interest = interest;
        }
        // A refusal clears the last answer, so the next send surfaces a pause.
        catch (HttpRequestException ex) when ((int?)ex.StatusCode is 401 or 403 or 404 or 409 or 426 or 428)
        {
            state.Interest = null;
            _logger.LogDebug("'{Peer}' refused to say what it wants ({Status}). Sending everything until it answers, so the refusal surfaces as a pause", FormatUtilities.SanitizeForLog(peer.DisplayName), (int?)ex.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not read what '{Peer}' wants. Keeping the last answer", FormatUtilities.SanitizeForLog(peer.DisplayName));
        }
    }

    /// <summary>Whether a peer's sent rows are due a recovery check, which runs every few minutes and not on every pass.</summary>
    private bool CheckDue(string peerKey, DateTime now)
    {
        if (_lastStatusCheck.TryGetValue(peerKey, out var last) && now - last < StatusCheckSpacing)
        {
            return false;
        }

        _lastStatusCheck[peerKey] = now;
        return true;
    }

    /// <summary>
    /// Reads a peer's queue status, completes the sent rows it finished, and sends again the ones it lost.
    /// A hint id is reused for every edit of an object, so a completion counts only at the stamp the row carries.
    /// </summary>
    private async Task RecoverLostAsync(SourceServer peer, SourceServerClient client, IList<OutboundHint> sent, CancellationToken cancellationToken)
    {
        QueueStatusResponse? status;
        try
        {
            status = await client.GetPeerQueueStatusAsync(_applicationHost.SystemId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not read the hint queue on '{Peer}'", FormatUtilities.SanitizeForLog(peer.DisplayName));
            return;
        }

        if (status is null)
        {
            return;
        }

        var byId = sent.ToDictionary(r => r.HintId, StringComparer.Ordinal);
        var held = new HashSet<string>(status.Inbound.Select(i => i.HintId), StringComparer.Ordinal);

        var completed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var done in status.CompletedHints ?? new List<CompletedHint>())
        {
            if (byId.TryGetValue(done.HintId, out var row) && UtcTime.AsUtc(done.VersionTimestamp) >= UtcTime.AsUtc(row.VersionTimestamp))
            {
                completed.Add(done.HintId);
            }
        }

        var (finished, lostIds) = HintDelivery.Reconcile(sent.Select(r => r.HintId), held, completed);
        // A peer with a standard user's API Key cannot report, so its status does.
        if (finished.Count > 0)
        {
            var reported = (status.CompletedHints ?? new List<CompletedHint>()).GroupBy(c => c.HintId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            Complete(peer, finished.Select(id =>
            {
                var done = reported.GetValueOrDefault(id);
                return new CompletedHint
                {
                    HintId = id,
                    VersionTimestamp = byId[id].VersionTimestamp,
                    Code = done?.Code ?? HintCode.Applied,
                    Reason = done?.Reason
                };
            }).ToList());
            _logger.LogDebug("'{Peer}' finished {Count} hint(s) it could not report. Completed from its status", FormatUtilities.SanitizeForLog(peer.DisplayName), finished.Count);
        }

        var lost = sent.Where(row => lostIds.Contains(row.HintId)).ToList();
        if (lost.Count > 0)
        {
            _outbound.Resend(lost.Select(row => row.Id), DateTime.UtcNow);
            _logger.LogWarning("'{Peer}' lost {Count} hint(s) before completing them, sending again", FormatUtilities.SanitizeForLog(peer.DisplayName), lost.Count);
            await _activity.ResentAsync(peer.DisplayName, lost).ConfigureAwait(false);
        }
    }

    /// <summary>Cuts a peer's answer body to 200 characters, or names it as having no detail.</summary>
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
