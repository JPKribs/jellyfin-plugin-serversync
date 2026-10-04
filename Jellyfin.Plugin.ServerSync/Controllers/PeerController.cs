using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// Endpoints another Server Sync installation calls to negotiate state with this server. Every route
/// sits behind Jellyfin's own RequiresElevation policy, so the caller must present an API key or an
/// administrator token that this server issued and validated. The plugin adds no authentication of
/// its own, and ids in a request are always this server's ids, never paths or foreign ids.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("ServerSync/Peer")]
[Produces(MediaTypeNames.Application.Json)]
public class PeerController : ControllerBase
{
    private readonly PeerHistoryService _history;
    private readonly IPluginConfigurationManager _configManager;
    private readonly IServerApplicationHost _applicationHost;
    private readonly InboundHintStore _inbound;
    private readonly OutboundHintStore _outbound;
    private readonly VersionStore _versions;
    private readonly InboundHintWorker _inboundWorker;
    private readonly PeerPairingStore _pairings;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly Microsoft.Extensions.Logging.ILogger<PeerController> _logger;

    // A pairing call is one small request, so a peer that cannot answer it promptly is treated as unreachable.
    private static readonly TimeSpan PairTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerController"/> class.
    /// </summary>
    /// <param name="history">The history negotiation service.</param>
    /// <param name="configManager">Plugin configuration manager.</param>
    /// <param name="applicationHost">The server host, for this server's id.</param>
    /// <param name="inbound">The inbound hint store.</param>
    /// <param name="outbound">The outbound hint store.</param>
    /// <param name="versions">The version store.</param>
    /// <param name="inboundWorker">The worker that applies inbound hints.</param>
    /// <param name="pairings">The pairing secrets.</param>
    /// <param name="clientFactory">Creates clients for the pairing calls.</param>
    /// <param name="logger">Logger.</param>
    public PeerController(
        PeerHistoryService history,
        IPluginConfigurationManager configManager,
        IServerApplicationHost applicationHost,
        InboundHintStore inbound,
        OutboundHintStore outbound,
        VersionStore versions,
        InboundHintWorker inboundWorker,
        PeerPairingStore pairings,
        ISourceServerClientFactory clientFactory,
        Microsoft.Extensions.Logging.ILogger<PeerController> logger)
    {
        _history = history;
        _configManager = configManager;
        _applicationHost = applicationHost;
        _inbound = inbound;
        _outbound = outbound;
        _versions = versions;
        _inboundWorker = inboundWorker;
        _pairings = pairings;
        _clientFactory = clientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Describes what this installation can negotiate, so a caller can tell a missing plugin from a
    /// missing feature before it queues any work.
    /// </summary>
    /// <returns>The capabilities.</returns>
    [HttpGet("Capabilities")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PeerCapabilities> GetCapabilities()
    {
        return Ok(new PeerCapabilities
        {
            ServerId = _applicationHost.SystemId,
            PluginVersion = _configManager.PluginVersion,
            Features = new List<string> { PeerHistoryNegotiator.HistoryFeature, HintProtocol.HintFeature },
            Accepts = HintProtocol.AcceptedKinds(_configManager.Configuration).Select(k => k.ToString()).ToList(),
            ServerTime = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Tells a server whether, and how, this server lists it. Hints from it are only accepted when it is
    /// listed as Pull or Sync, so a server about to turn on Send mode asks this first.
    /// </summary>
    /// <param name="serverId">The asking server's Jellyfin id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The listing.</returns>
    [HttpGet("Link")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<PeerLinkResponse>> Link([FromQuery] string? serverId, CancellationToken cancellationToken)
    {
        var response = new PeerLinkResponse { ServerId = _applicationHost.SystemId, ServerName = _applicationHost.FriendlyName };
        var entry = string.IsNullOrWhiteSpace(serverId)
            ? null
            : _configManager.Configuration.FindServerById(serverId);
        if (entry is null)
        {
            return Ok(response);
        }

        response.Listed = true;
        response.Mode = entry.Mode.ToString();
        response.Enabled = entry.IsEnabled && entry.IsConfigured;
        response.PullsFromYou = entry.Pulls;
        response.SendsToYou = entry.Pushes;

        // The caller says it is this entry. Anyone holding an administrator's key could say so, which
        // is why the secret goes to the entry's own address over this server's own key for it, not back
        // to the caller. Only the real server at that address receives it, and it is what every later
        // request must carry.
        if (response.Enabled)
        {
            (response.Paired, response.PairingError) = await PairAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            response.PairingError = "the entry for this server is disabled or has no URL and key";
        }

        return Ok(response);
    }

    /// <summary>
    /// A peer hands this server the secret it must present on requests that name it as the sender to
    /// that peer. The peer makes the call over its own connection to this server, so the secret only
    /// ever arrives from a server this one is configured to reach.
    /// </summary>
    /// <param name="request">The issuing server's id and the secret.</param>
    /// <returns>Whether the secret was kept.</returns>
    [HttpPost("Pair")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<object> Pair([FromBody] PairRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ServerId) || string.IsNullOrWhiteSpace(request.Secret) || request.Secret.Length > 256)
        {
            return BadRequest("A request body with ServerId and a Secret of at most 256 characters is required");
        }

        var entry = _configManager.Configuration.FindServerById(request.ServerId);
        if (entry is null)
        {
            return Conflict($"server {request.ServerId} is not configured on this server, so it cannot pair");
        }

        _pairings.SetOutbound(entry.Key, request.Secret);
        _logger.LogInformation("Paired with '{Peer}': it issued the secret this server presents to it", entry.DisplayName);
        return Ok(new { Paired = true });
    }

    // Hands an entry its secret by calling the entry's own address. The secret already issued is sent
    // again when there is one, so a peer that lost its copy is whole again the moment it checks the link.
    // Pairings with one entry run one at a time, so two checks at once send the same secret.
    private async Task<(bool Paired, string? Error)> PairAsync(Models.Configuration.SourceServer entry, CancellationToken cancellationToken)
    {
        var gate = _pairings.PairingLock(entry.Key);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await PairOnceAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<(bool Paired, string? Error)> PairOnceAsync(Models.Configuration.SourceServer entry, CancellationToken cancellationToken)
    {
        string secret;
        try
        {
            secret = _pairings.GetInbound(entry.Key) ?? PeerPairingStore.NewSecret();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(PairTimeout);
            using var client = _clientFactory.Create(entry);
            var (status, body) = await client.PairAsync(new PairRequest { ServerId = _applicationHost.SystemId, Secret = secret }, deadline.Token).ConfigureAwait(false);
            if (status is 401 or 403)
            {
                // A standard user's key cannot reach the peer's endpoint, so no secret can be issued. The
                // peer is then taken on its word, which is what the operator chose with that key.
                _pairings.MarkInboundRefused(entry.Key);
                var why = "this server holds a standard user's key for it, so it cannot be issued a secret and its hints are accepted on its word";
                _logger.LogWarning("Could not pair with '{Peer}': {Why}", entry.DisplayName, why);
                return (false, why);
            }

            if (status is < 200 or >= 300)
            {
                var why = status switch
                {
                    404 => "its Server Sync predates pairing",
                    409 => "it does not list this server: " + body,
                    _ => $"it answered {status}: {body}"
                };
                _logger.LogWarning("Could not pair with '{Peer}': {Why}", entry.DisplayName, why);
                return (false, why);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not pair with '{Peer}'", entry.DisplayName);
            return (false, "this server could not reach it at " + entry.Url + ": " + ex.Message);
        }

        try
        {
            _pairings.SetInbound(entry.Key, secret);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Paired with '{Peer}' but could not store its secret", entry.DisplayName);
            return (false, "the secret could not be stored on this server: " + ex.Message);
        }

        _logger.LogInformation("Paired with '{Peer}': it now presents a secret this server issued", entry.DisplayName);
        return (true, null);
    }

    // The background check of a refused pairing. It runs after the request that started it has ended, so
    // it logs its own failures.
    private async Task RecheckPairingAsync(Models.Configuration.SourceServer entry)
    {
        try
        {
            await PairAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check the pairing with '{Peer}' again", entry.DisplayName);
        }
    }

    // The entry a request's sender claims to be, proven by the secret this server issued to that entry,
    // or the answer to send back. An entry without a secret has never paired, so the sender pairs on
    // hearing so and comes back.
    private (Models.Configuration.SourceServer? Entry, ActionResult? Refusal) Sender(string? senderServerId)
    {
        if (string.IsNullOrWhiteSpace(senderServerId))
        {
            return (null, BadRequest("A SenderServerId is required"));
        }

        var entry = _configManager.Configuration.FindServerById(senderServerId);
        if (entry is null)
        {
            return (null, Conflict($"server {senderServerId} is not configured on this server"));
        }

        var presented = Request.Headers[HintProtocol.PairingHeader].FirstOrDefault();
        var expected = _pairings.GetInbound(entry.Key);
        if (expected is null && _pairings.InboundRefusedAt(entry.Key) is not null)
        {
            // This server could not issue it a secret the last time, so it is taken on its word. Whether
            // the entry now holds an administrator's key is checked again in the background, at most once
            // a minute, so the exemption ends soon after it does without holding up this request.
            if (_pairings.TryClaimRecheck(entry.Key, HintProtocol.CapabilityRefresh))
            {
                _ = Task.Run(() => RecheckPairingAsync(entry), CancellationToken.None);
            }

            return (entry, null);
        }

        if (!PeerPairingStore.Matches(presented, expected))
        {
            return (null, StatusCode(HintProtocol.UnpairedStatus, $"server {senderServerId} is not paired with this server. Its Server Sync pairs by checking the link to this server"));
        }

        return (entry, null);
    }

    /// <summary>
    /// Receives change hints from a peer. Each hint is stored durably and answered as accepted, which
    /// means queued, not applied. The sender must be configured here as a Pull or Sync server, since
    /// that is where the change is pulled from. A hint for something this server does not map is still
    /// answered, as not accepted with the reason, because the sender knows nothing of this server's
    /// mappings and must not keep trying.
    /// </summary>
    /// <param name="request">The hints.</param>
    /// <returns>One result per hint.</returns>
    [HttpPost("Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<QueueResponse> Queue([FromBody] QueueRequest request)
    {
        if (request is null || request.Items is null || string.IsNullOrWhiteSpace(request.SenderServerId))
        {
            return BadRequest("A request body with SenderServerId and Items is required");
        }

        if (request.Items.Count > HintProtocol.MaxHintsPerRequest)
        {
            return BadRequest($"At most {HintProtocol.MaxHintsPerRequest} hints per request");
        }

        var (origin, refusal) = Sender(request.SenderServerId);
        if (origin is null)
        {
            return refusal!;
        }

        if (!origin.Pulls)
        {
            return Conflict($"server {request.SenderServerId} is not configured on this server as a Pull or Sync server, so its changes cannot be pulled");
        }

        // A hint with no id cannot be answered, since the sender matches answers by id, so it fails the
        // request. Every other problem with one hint is declined for that hint alone, so one bad row
        // never takes the rest of the batch down with it.
        if (request.Items.Any(h => string.IsNullOrWhiteSpace(h.HintId)))
        {
            return BadRequest("Every hint needs a HintId");
        }

        // One origin cannot fill the queue without bound. A full queue answers "busy" so the sender waits
        // and sends again, rather than this server dropping the rows that are next to apply.
        if (_inbound.CountForOrigin(request.SenderServerId) >= HintProtocol.MaxPendingPerPeer)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, $"this server already holds {HintProtocol.MaxPendingPerPeer} hints from {origin.DisplayName}. Send again later");
        }

        var response = new QueueResponse();
        var now = DateTime.UtcNow;
        foreach (var hint in request.Items)
        {
            var result = new QueueResult { HintId = hint.HintId };
            response.Items.Add(result);

            var reason = string.IsNullOrWhiteSpace(hint.Key) ? "the hint carries no key"
                : !string.Equals(hint.OriginServerId, request.SenderServerId, StringComparison.OrdinalIgnoreCase) ? "a hint's origin must be the sender, since hints are never forwarded"
                : HintProtocol.IsFutureVersion(hint.VersionTimestamp, now) ? $"the hint's version is dated more than {HintProtocol.MaxVersionLead.TotalMinutes:0} minutes ahead of this server's clock. Check the clock on the sender"
                : Unmappable(origin, hint);
            if (reason is not null)
            {
                result.Accepted = false;
                result.Reason = reason;
                continue;
            }

            _inbound.Enqueue(InboundHint.FromHint(hint, now));
            result.Accepted = true;
        }

        if (response.Items.Any(r => r.Accepted))
        {
            _inboundWorker.Wake();
        }

        return Ok(response);
    }

    /// <summary>A peer reports hints this server sent it as done. The matching outbound rows are removed.</summary>
    /// <param name="request">The finished hints.</param>
    /// <returns>How many rows were removed.</returns>
    [HttpPost("Complete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<object> Complete([FromBody] CompleteRequest request)
    {
        if (request is null || request.Items is null)
        {
            return BadRequest("A request body with SenderServerId and Items is required");
        }

        var (peer, refusal) = Sender(request.SenderServerId);
        if (peer is null)
        {
            return refusal!;
        }

        if (request.Items.Count > HintProtocol.MaxHintsPerRequest)
        {
            return BadRequest($"At most {HintProtocol.MaxHintsPerRequest} completions per request");
        }

        // Only the rows queued for the peer that reports them, and never past this server's clock, so a
        // peer can finish its own work and nothing else.
        var removed = _outbound.Complete(peer.Key, request.Items);
        return Ok(new { Removed = removed });
    }

    /// <summary>Lists this server's inbound queue, so a peer can tell whether work it sent is still held.</summary>
    /// <param name="serverId">The asking server's id, to list only its own hints, or null for all.</param>
    /// <returns>The inbound rows.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<QueueStatusResponse> Status([FromQuery] string? serverId)
    {
        // An origin asks about its own hints only. Hint ids start with the origin's server id, so both
        // the queue and the completions can be narrowed to it.
        var prefix = string.IsNullOrWhiteSpace(serverId) ? null : serverId + ":";
        var completed = _inboundWorker.RecentlyCompleted
            .Where(c => prefix is null || c.HintId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var inbound = prefix is null ? _inbound.GetAll() : _inbound.GetForOrigin(serverId!);
        return Ok(new QueueStatusResponse
        {
            ServerId = _applicationHost.SystemId,
            Inbound = inbound.Select(InboundHintDto.From).ToList(),
            Completed = completed.Select(c => c.HintId).ToList(),
            CompletedHints = completed
        });
    }

    /// <summary>Returns the versions this server holds for a batch of its own keys. Keys with no version are left out.</summary>
    /// <param name="request">The kind and keys.</param>
    /// <returns>The versions.</returns>
    [HttpPost("Versions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<VersionsResponse> Versions([FromBody] VersionsRequest request)
    {
        if (request is null || request.Keys is null)
        {
            return BadRequest("A request body with Keys is required");
        }

        if (request.Keys.Count > HintProtocol.MaxHintsPerRequest)
        {
            return BadRequest($"At most {HintProtocol.MaxHintsPerRequest} keys per request");
        }

        return Ok(new VersionsResponse { Items = _versions.GetMany(request.Kind, request.Keys).ToList() });
    }

    // What makes a hint impossible to apply here, or null when it can be queued. Decided on receipt so
    // the sender hears straight away and the row never sits in the queue.
    private string? Unmappable(Models.Configuration.SourceServer origin, SyncHint hint)
    {
        var config = _configManager.Configuration;
        switch (hint.Kind)
        {
            case HintKind.Metadata:
                if (!config.EnableMetadataSync)
                {
                    return "metadata sync is off on this server";
                }

                if (!Guid.TryParse(hint.Key, out _))
                {
                    return "the key is not an item id";
                }

                return HintMapping.FindBySourcePath(origin, hint.ItemPath) is null ? "the path is not in a library mapped on this server" : null;

            case HintKind.People:
                if (!config.EnablePeopleSync)
                {
                    return "people sync is off on this server";
                }

                return string.IsNullOrWhiteSpace(hint.Key) ? "the hint carries no person name" : null;

            case HintKind.Content:
                if (!config.EnableContentSync)
                {
                    return "content sync is off on this server";
                }

                if (!Guid.TryParse(hint.Key, out _))
                {
                    return "the key is not an item id";
                }

                return HintMapping.FindBySourcePath(origin, hint.ItemPath) is null ? "the path is not in a library mapped on this server" : null;

            case HintKind.Users:
                // Never announced live. Jellyfin raises no event for policy and configuration changes,
                // so user settings travel on the scheduled task only.
                return "user settings are not announced live, the scheduled Sync Information task carries them";

            case HintKind.History:
                if (!config.EnableHistorySync)
                {
                    return "history sync is off on this server";
                }

                if (!HintProtocol.TryParseHistoryKey(hint.Key, out var userId, out _))
                {
                    return "the key is not a user id and item id";
                }

                if (HintMapping.FindBySourceUser(origin, userId) is null)
                {
                    return "the user is not mapped on this server";
                }

                if (HintMapping.FindBySourcePath(origin, hint.ItemPath) is null)
                {
                    return "the path is not in a library mapped on this server";
                }

                return null;

            default:
                return $"this version does not apply {hint.Kind} hints";
        }
    }

    /// <summary>
    /// Applies a batch of merged watch history states. Each entry is written only when this server's
    /// live state still matches what the caller expected, so a play that happened here after the caller
    /// read this server is reported back as stale instead of being overwritten.
    /// </summary>
    /// <param name="request">The proposed writes.</param>
    /// <returns>One result per entry, in request order.</returns>
    [HttpPost("History")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<PeerHistoryResponse> NegotiateHistory([FromBody] PeerHistoryRequest request)
    {
        if (request is null || request.Items is null)
        {
            return BadRequest("A request body with Items is required");
        }

        if (request.Items.Count > PeerHistoryNegotiator.MaxEntriesPerRequest)
        {
            return BadRequest($"At most {PeerHistoryNegotiator.MaxEntriesPerRequest} entries per request");
        }

        // A sender this server lists is recorded against its own rows, so it has to prove it is that
        // entry. A sender this server does not list negotiates as any administrator may, and nothing is
        // recorded against anyone's row.
        if (!string.IsNullOrWhiteSpace(request.SenderServerId)
            && _configManager.Configuration.FindServerById(request.SenderServerId) is not null)
        {
            var (sender, refusal) = Sender(request.SenderServerId);
            if (sender is null)
            {
                return refusal!;
            }
        }

        return Ok(_history.Negotiate(request));
    }
}
