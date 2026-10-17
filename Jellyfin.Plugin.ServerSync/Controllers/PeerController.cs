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
    private readonly OutboundHintWorker _outboundWorker;
    private readonly PeerPairingStore _pairings;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly HintTriage _triage;
    private readonly PeerItemLookup _lookup;
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
        OutboundHintWorker outboundWorker,
        PeerPairingStore pairings,
        ISourceServerClientFactory clientFactory,
        HintTriage triage,
        PeerItemLookup lookup,
        Microsoft.Extensions.Logging.ILogger<PeerController> logger)
    {
        _triage = triage;
        _lookup = lookup;
        _history = history;
        _configManager = configManager;
        _applicationHost = applicationHost;
        _inbound = inbound;
        _outbound = outbound;
        _versions = versions;
        _inboundWorker = inboundWorker;
        _outboundWorker = outboundWorker;
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
    [RequireSameVersion]
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
            if (status is < 200 or >= 300)
            {
                var why = status switch
                {
                    401 or 403 => "it refused the key this server holds for it, so it could not be issued a secret. Check the key on this server's entry for it, and update Server Sync there if it is older than this server's",
                    404 => "Server Sync is not installed there",
                    426 => body ?? "its Server Sync is a different version",
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
        if (!PeerPairingStore.Matches(presented, _pairings.GetInbound(entry.Key)))
        {
            return (null, StatusCode(HintProtocol.UnpairedStatus, $"server {senderServerId} is not paired with this server. Its Server Sync pairs by checking the link to this server"));
        }

        return (entry, null);
    }

    /// <summary>
    /// Receives change hints from a peer. Each hint is answered with a code: Queued and NoLocalItem are
    /// stored, the latter parked until the local item arrives, and every other code means this server
    /// will not use it, so the sender stops trying. The sender must be configured here as a Pull or
    /// Sync server, since that is where the change is pulled from.
    /// </summary>
    /// <param name="request">The hints.</param>
    /// <returns>One result per hint.</returns>
    [HttpPost("Queue")]
    [RequireSameVersion]
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
        var parkedAny = false;
        foreach (var hint in request.Items)
        {
            var assessment = _triage.Assess(origin, hint, request.SenderServerId, now);
            response.Items.Add(new QueueResult { HintId = hint.HintId, Code = assessment.Code, Reason = assessment.Reason });
            if (!assessment.Keep)
            {
                continue;
            }

            var row = InboundHint.FromHint(hint, now);
            if (assessment.ParkKey is not null)
            {
                row.ParkedPath = assessment.ParkKey;
                row.NextAttempt = now + HintProtocol.ParkWindow;
                row.LastError = assessment.Reason;
            }

            _inbound.Enqueue(row);
            if (assessment.ParkKey is not null)
            {
                parkedAny = true;
                if (_triage.Present(assessment.ParkKey, hint.Kind == HintKind.People ? hint.Key : null))
                {
                    _inbound.WakeParked(assessment.ParkKey, DateTime.UtcNow);
                    response.Items[^1].Code = HintCode.Queued;
                }
            }
        }

        if (parkedAny)
        {
            _inbound.TrimParked(request.SenderServerId, HintProtocol.MaxPendingPerPeer);
        }

        if (response.Items.Any(r => r.Code == HintCode.Queued))
        {
            _inboundWorker.Wake();
        }

        return Ok(response);
    }

    /// <summary>A peer reports hints this server sent it as done. The matching outbound rows are removed.</summary>
    /// <param name="request">The finished hints.</param>
    /// <returns>How many rows were removed.</returns>
    [HttpPost("Complete")]
    [RequireSameVersion]
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
        var removed = _outboundWorker.Complete(peer, request.Items);
        return Ok(new { Removed = removed });
    }

    /// <summary>Lists the hints a paired peer sent that this server still holds, and those it finished.</summary>
    /// <param name="serverId">The asking server's id.</param>
    /// <returns>The inbound rows.</returns>
    [HttpGet("Status")]
    [RequireSameVersion]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<QueueStatusResponse> Status([FromQuery] string? serverId)
    {
        var (peer, refusal) = Sender(serverId);
        if (peer is null)
        {
            return refusal!;
        }

        var prefix = serverId + ":";
        var completed = _inboundWorker.RecentlyCompleted
            .Where(c => c.HintId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Ok(new QueueStatusResponse
        {
            ServerId = _applicationHost.SystemId,
            Inbound = _inbound.GetForOrigin(serverId!).Select(InboundHintDto.From).ToList(),
            CompletedHints = completed
        });
    }

    [HttpGet("Interest")]
    [RequireSameVersion]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(HintProtocol.UnpairedStatus)]
    public ActionResult<PeerInterest> Interest([FromQuery] string? serverId)
    {
        var (peer, refusal) = Sender(serverId);
        if (peer is null)
        {
            return refusal!;
        }

        return Ok(peer.Pulls ? PeerInterest.For(_configManager.Configuration, peer) : new PeerInterest { Revision = "none" });
    }

    [HttpPost("Lookup")]
    [RequireSameVersion]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<PeerLookupResponse> Lookup([FromBody] PeerLookupRequest request)
    {
        if (request is null || (string.IsNullOrWhiteSpace(request.Path) && string.IsNullOrWhiteSpace(request.Name) && request.ProviderIds is not { Count: > 0 } && request.SeriesProviderIds is not { Count: > 0 }))
        {
            return BadRequest("A request body with a Path, a Name, or provider ids is required");
        }

        if (request.Path?.Length > 4096 || request.Name?.Length > 512 || request.ItemType?.Length > 128
            || request.ProviderIds?.Count > PeerItemLookup.MaxProviderIds || request.SeriesProviderIds?.Count > PeerItemLookup.MaxProviderIds)
        {
            return BadRequest("The Path, Name, or ItemType is too long");
        }

        return Ok(_lookup.Find(request));
    }

    /// <summary>Returns the versions this server holds for a batch of its own keys. Keys with no version are left out.</summary>
    /// <param name="request">The kind and keys.</param>
    /// <returns>The versions.</returns>
    [HttpPost("Versions")]
    [RequireSameVersion]
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

    /// <summary>
    /// Applies a batch of merged watch history states. Each entry is written only when this server's
    /// live state still matches what the caller expected, so a play that happened here after the caller
    /// read this server is reported back as stale instead of being overwritten.
    /// </summary>
    /// <param name="request">The proposed writes.</param>
    /// <returns>One result per entry, in request order.</returns>
    [HttpPost("History")]
    [RequireSameVersion]
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
