using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// The routes another server's Server Sync calls. Every route needs a signed in caller the Whitelist allows, and a standard user is held to its own data.
/// </summary>
[ApiController]
[Authorize]
[Route("Sync/Peer")]
[PeerCallerGuard]
[RequestSizeLimit(MaxRequestBytes)]
[Produces(MediaTypeNames.Application.Json)]
public partial class PeerController : ControllerBase
{
    private readonly PeerHistoryService _history;
    private readonly IPluginConfigurationManager _configManager;
    private readonly IServerApplicationHost _applicationHost;
    private readonly InboundHintStore _inbound;
    private readonly VersionStore _versions;
    private readonly InboundHintWorker _inboundWorker;
    private readonly OutboundHintWorker _outboundWorker;
    private readonly PeerPairingStore _pairings;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly HintTriage _triage;
    private readonly PeerItemLookup _lookup;
    private readonly ILogger<PeerController> _logger;

    /// <summary>
    /// The largest body a peer route reads, which fits the fullest batch of hints with room to spare.
    /// </summary>
    public const long MaxRequestBytes = 4 * 1024 * 1024;

    /// <summary>
    /// How long a pairing call may take before the peer counts as unreachable.
    /// </summary>
    private static readonly TimeSpan PairTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerController"/> class.
    /// </summary>
    /// <param name="history">Applies the History a peer proposes.</param>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="applicationHost">Gives this server's ID and name.</param>
    /// <param name="inbound">Holds the hints peers sent that are not yet applied.</param>
    /// <param name="outbound">Holds the hints owed to peers, which these routes do not read.</param>
    /// <param name="versions">Reads the stamp an object carries.</param>
    /// <param name="inboundWorker">Applies received hints, and is woken when one is queued.</param>
    /// <param name="outboundWorker">Removes the hints a peer reports done.</param>
    /// <param name="pairings">Holds the secret issued to each paired server.</param>
    /// <param name="clientFactory">Builds the client for another server.</param>
    /// <param name="triage">Decides what to do with each inbound hint.</param>
    /// <param name="lookup">Finds this server's item for a peer's description of one.</param>
    /// <param name="logger">Writes log entries.</param>
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
        ILogger<PeerController> logger)
    {
        _triage = triage;
        _lookup = lookup;
        _history = history;
        _configManager = configManager;
        _applicationHost = applicationHost;
        _inbound = inbound;
        _versions = versions;
        _inboundWorker = inboundWorker;
        _outboundWorker = outboundWorker;
        _pairings = pairings;
        _clientFactory = clientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Describes what this server can negotiate, so a caller can tell a missing plugin from a missing feature before it queues work.
    /// </summary>
    /// <returns>This server's protocol versions, accepted kinds, and features.</returns>
    [HttpGet("Capabilities")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PeerCapabilities> GetCapabilities()
    {
        return Ok(new PeerCapabilities
        {
            ServerId = _applicationHost.SystemId,
            PluginVersion = _configManager.PluginVersion,
            ProtocolVersion = HintProtocol.ProtocolVersion,
            MinProtocolVersion = HintProtocol.MinProtocolVersion,
            Accepts = HintProtocol.AcceptedKinds(_configManager.Configuration).Select(k => k.ToString()).ToList(),
            Features = Services.Links.LinkFeature.Offered(_configManager.Configuration) ? new List<string> { Services.Links.LinkFeature.Name } : new List<string>(),
            ServerTime = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Tells a server whether and how this server lists it, and pairs with it when it is listed.
    /// </summary>
    /// <param name="serverId">The Jellyfin ID of the server that asks.</param>
    /// <param name="cancellationToken">Cancels the pairing call.</param>
    /// <returns>How this server lists the caller, and whether the two are paired.</returns>
    [HttpGet("Link")]
    [RequireCompatibleProtocol]
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
        response.AccessLevel = string.IsNullOrEmpty(entry.AccessLevel) ? null : entry.AccessLevel;
        response.AccessUser = string.IsNullOrEmpty(entry.AuthenticatedUser) ? null : entry.AuthenticatedUser;
        response.CanDownload = entry.CanDownload;

        // Any caller could claim this entry, so the secret goes to its own address.
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
    /// Hands an entry its secret by calling the entry's own address, sending the issued secret again when there is one.
    /// Pairings with one entry run one at a time, so two checks at once send the same secret.
    /// </summary>
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

    /// <summary>Sends the secret to the entry's address and stores it once the entry accepts it.</summary>
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
                _logger.LogWarning("Could not pair with '{Peer}': {Why}", FormatUtilities.SanitizeForLog(entry.DisplayName), FormatUtilities.SanitizeForLog(why, 300));
                return (false, why);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not pair with '{Peer}'", FormatUtilities.SanitizeForLog(entry.DisplayName));
            return (false, "this server could not reach it at " + entry.Url + ": " + ex.Message);
        }

        try
        {
            _pairings.SetInbound(entry.Key, secret);
        }
        // A secret that cannot be stored means not paired, whatever the cause.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Paired with '{Peer}' but could not store its secret", FormatUtilities.SanitizeForLog(entry.DisplayName));
            return (false, "the secret could not be stored on this server: " + ex.Message);
        }

        _logger.LogInformation("Paired with '{Peer}': it now presents a secret this server issued", FormatUtilities.SanitizeForLog(entry.DisplayName));
        return (true, null);
    }

    /// <summary>
    /// Finds the entry a request's sender claims to be, proven by the secret this server issued to it, or the refusal to send back.
    /// An entry without a secret has never paired, so the sender pairs on hearing so and comes back.
    /// </summary>
    private (Models.Configuration.SourceServer? Entry, ActionResult? Refusal) Sender(string? senderServerId)
    {
        if (string.IsNullOrWhiteSpace(senderServerId))
        {
            return (null, BadRequest("A SenderServerId is required."));
        }

        var entry = _configManager.Configuration.FindServerById(senderServerId);
        if (entry is null)
        {
            return (null, Conflict($"server {senderServerId} is not configured on this server"));
        }

        // This server skips a disabled entry, so it takes nothing from it either.
        if (!entry.IsEnabled)
        {
            return (null, Conflict($"server {senderServerId} is disabled on this server"));
        }

        var presented = Request.Headers[HintProtocol.PairingHeader].FirstOrDefault();
        if (!PeerPairingStore.Matches(presented, _pairings.GetInbound(entry.Key)))
        {
            return (null, StatusCode(HintProtocol.UnpairedStatus, $"server {senderServerId} is not paired with this server. Its Server Sync pairs by checking the link to this server"));
        }

        return (entry, null);
    }

    /// <summary>
    /// Receives hints from a peer that this server lists as Pull or Sync, and answers each with a code.
    /// Queued and NoLocalItem are stored, and any other code tells the sender to stop trying.
    /// </summary>
    /// <param name="request">The sender's ID and its batch of hints.</param>
    /// <returns>One result per hint.</returns>
    [HttpPost("Queue")]
    [RequireCompatibleProtocol]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<QueueResponse> Queue([FromBody] QueueRequest request)
    {
        if (request is null || request.Items is null || string.IsNullOrWhiteSpace(request.SenderServerId))
        {
            return BadRequest("A request body with SenderServerId and Items is required.");
        }

        if (request.Items.Count > HintProtocol.MaxHintsPerRequest)
        {
            return BadRequest($"At most {HintProtocol.MaxHintsPerRequest} hints per request.");
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

        // The sender matches answers by ID, so a hint without one fails the request.
        if (request.Items.Any(h => h is null || string.IsNullOrWhiteSpace(h.HintId) || h.HintId.Length > HintTriage.MaxIdLength))
        {
            return BadRequest($"Every hint needs a HintId of at most {HintTriage.MaxIdLength} characters.");
        }

        // A full queue answers busy, so the sender waits and no row is dropped.
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
    /// <param name="request">The sender's ID and the hints it finished.</param>
    /// <returns>How many rows were removed.</returns>
    [HttpPost("Complete")]
    [RequireCompatibleProtocol]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<object> Complete([FromBody] CompleteRequest request)
    {
        if (request is null || request.Items is null)
        {
            return BadRequest("A request body with SenderServerId and Items is required.");
        }

        var (peer, refusal) = Sender(request.SenderServerId);
        if (peer is null)
        {
            return refusal!;
        }

        if (request.Items.Count > HintProtocol.MaxHintsPerRequest || request.Items.Any(i => i is null))
        {
            return BadRequest($"At most {HintProtocol.MaxHintsPerRequest} completions per request, none of them null.");
        }

        var removed = _outboundWorker.Complete(peer, request.Items);
        return Ok(new { Removed = removed });
    }

    /// <summary>Lists the hints a paired peer sent that this server still holds, and those it finished.</summary>
    /// <param name="serverId">The Jellyfin ID of the server that asks.</param>
    /// <returns>The rows still held and the hints finished recently.</returns>
    [HttpGet("Status")]
    [RequireCompatibleProtocol]
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

    /// <summary>Says what this server reads from the sender, so the sender announces only what is wanted.</summary>
    /// <param name="serverId">The Jellyfin ID of the server that asks.</param>
    /// <returns>What this server wants announced.</returns>
    [HttpGet("Interest")]
    [RequireCompatibleProtocol]
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

    /// <summary>Lists the playlists the caller may read.</summary>
    /// <param name="reader">Reads this server's playlists.</param>
    /// <param name="id">One playlist to read, or null for every one.</param>
    /// <param name="serverId">The Jellyfin ID of the server that asks.</param>
    /// <returns>The playlists with their items, owners, and shares.</returns>
    [HttpGet("Playlists")]
    [RequireCompatibleProtocol]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PeerPlaylistsResponse> Playlists([FromServices] Services.Playlists.PeerPlaylistReader reader, [FromQuery] Guid? id, [FromQuery] string? serverId)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var entry = string.IsNullOrWhiteSpace(serverId) ? null : _configManager.Configuration.FindServerById(serverId);
        var owners = entry?.GetEnabledUserMappings()
            .Select(m => Guid.TryParse(m.LocalUserId, out var owner) ? owner : Guid.Empty)
            .Where(owner => owner != Guid.Empty)
            .ToList();
        return Ok(reader.ReadAll(id, PeerCallerGuard.ScopedUser(HttpContext), owners));
    }

    /// <summary>Finds the local item that matches a path, a name, or provider ids.</summary>
    /// <param name="request">The path, name, or provider IDs to match on.</param>
    /// <param name="userManager">Finds the user a standard caller is held to.</param>
    /// <param name="libraryManager">Finds the matched item, to check that user may see it.</param>
    /// <returns>The matching item, when there is one.</returns>
    [HttpPost("Lookup")]
    [RequireCompatibleProtocol]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<PeerLookupResponse> Lookup([FromBody] PeerLookupRequest request, [FromServices] MediaBrowser.Controller.Library.IUserManager userManager, [FromServices] MediaBrowser.Controller.Library.ILibraryManager libraryManager)
    {
        if (request is null || (string.IsNullOrWhiteSpace(request.Path) && string.IsNullOrWhiteSpace(request.Name) && request.ProviderIds is not { Count: > 0 } && request.SeriesProviderIds is not { Count: > 0 }))
        {
            return BadRequest("A request body with a Path, a Name, or provider ids is required.");
        }

        if (request.Path?.Length > 4096 || request.Name?.Length > 512 || request.ItemType?.Length > 128
            || request.ProviderIds?.Count > PeerItemLookup.MaxProviderIds || request.SeriesProviderIds?.Count > PeerItemLookup.MaxProviderIds)
        {
            return BadRequest("The Path, Name, or ItemType is too long.");
        }

        var found = _lookup.Find(request);
        if (found.Found
            && PeerCallerGuard.ScopedUser(HttpContext) is { } scoped
            && (userManager.GetUserById(scoped) is not { } user
                || !Guid.TryParse(found.ItemId, out var itemId)
                || libraryManager.GetItemById(itemId) is not { } item
                || !item.IsVisible(user)))
        {
            return Ok(new PeerLookupResponse());
        }

        return Ok(found);
    }

    /// <summary>Reads the media segments of a batch of this server's items, leaving out the ones Server Sync copied here.</summary>
    /// <param name="request">The items whose segments are read.</param>
    /// <param name="reader">Reads each item's segments.</param>
    /// <returns>One entry for each item, in order.</returns>
    [HttpPost("Segments")]
    [RequireCompatibleProtocol]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PeerSegmentsResponse>> Segments([FromBody] PeerSegmentsRequest request, [FromServices] Services.Peer.PeerSegmentReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (request?.ItemIds is not { Count: > 0 } ids)
        {
            return BadRequest("A request body with ItemIds is required.");
        }

        if (ids.Count > PeerSegmentsRequest.MaxItems || ids.Any(id => id is null || id.Length > 64))
        {
            return BadRequest($"At most {PeerSegmentsRequest.MaxItems} ItemIds, each an item id, are allowed.");
        }

        return Ok(await reader.ReadAsync(ids, PeerCallerGuard.ScopedUser(HttpContext)).ConfigureAwait(false));
    }

    /// <summary>Returns the stamps this server holds for a batch of its own keys, leaving out keys with none.</summary>
    /// <param name="request">The kind and the keys to read.</param>
    /// <returns>The stamp of each key that has one.</returns>
    [HttpPost("Versions")]
    [RequireCompatibleProtocol]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<VersionsResponse> Versions([FromBody] VersionsRequest request)
    {
        if (request is null || request.Keys is null)
        {
            return BadRequest("A request body with Keys is required.");
        }

        if (request.Keys.Count > HintProtocol.MaxHintsPerRequest || request.Keys.Any(k => k is null))
        {
            return BadRequest($"At most {HintProtocol.MaxHintsPerRequest} keys per request, none of them null.");
        }

        var keys = request.Keys;
        if (PeerCallerGuard.ScopedUser(HttpContext) is { } scoped)
        {
            var own = scoped.ToString("N", System.Globalization.CultureInfo.InvariantCulture);
            keys = request.Kind switch
            {
                HintKind.History => keys.Where(k => HintProtocol.TryParseHistoryKey(k, out var userId, out _) && userId == scoped).ToList(),
                HintKind.Users => keys.Where(k => string.Equals(k, own, StringComparison.OrdinalIgnoreCase)).ToList(),
                _ => keys
            };
        }

        return Ok(new VersionsResponse { Items = _versions.GetMany(request.Kind, keys).ToList() });
    }

    /// <summary>
    /// Applies a batch of merged History states.
    /// Each is written only when this server's live state matches what the caller expected, so a play made here meanwhile is reported as stale.
    /// </summary>
    /// <param name="request">The sender's ID and the writes it proposes.</param>
    /// <returns>One result per entry, in request order.</returns>
    [HttpPost("History")]
    [RequireCompatibleProtocol]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<PeerHistoryResponse> NegotiateHistory([FromBody] PeerHistoryRequest request)
    {
        if (request is null || request.Items is null)
        {
            return BadRequest("A request body with Items is required.");
        }

        if (request.Items.Count > PeerHistoryNegotiator.MaxEntriesPerRequest || request.Items.Any(e => e is null))
        {
            return BadRequest($"At most {PeerHistoryNegotiator.MaxEntriesPerRequest} entries per request, none of them null.");
        }

        // A listed sender is recorded against its own rows, so it must prove itself.
        if (!string.IsNullOrWhiteSpace(request.SenderServerId)
            && _configManager.Configuration.FindServerById(request.SenderServerId) is not null)
        {
            var (sender, refusal) = Sender(request.SenderServerId);
            if (sender is null)
            {
                return refusal!;
            }
        }

        if (PeerCallerGuard.ScopedUser(HttpContext) is not { } scoped)
        {
            return Ok(_history.Negotiate(request));
        }

        var all = request.Items;
        request.Items = all.Where(e => Guid.TryParse(e.UserId, out var id) && id == scoped).ToList();
        var answered = new Queue<PeerHistoryResult>(_history.Negotiate(request).Items);
        var response = new PeerHistoryResponse();
        foreach (var entry in all)
        {
            response.Items.Add(Guid.TryParse(entry.UserId, out var id) && id == scoped && answered.Count > 0
                ? answered.Dequeue()
                : new PeerHistoryResult { UserId = entry.UserId, ItemId = entry.ItemId, Outcome = PeerHistoryOutcome.Failed, Reason = "a standard user may only negotiate its own history" });
        }

        return Ok(response);
    }
}
