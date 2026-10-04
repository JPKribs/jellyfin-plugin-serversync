using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

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
    public PeerController(
        PeerHistoryService history,
        IPluginConfigurationManager configManager,
        IServerApplicationHost applicationHost,
        InboundHintStore inbound,
        OutboundHintStore outbound,
        VersionStore versions,
        InboundHintWorker inboundWorker)
    {
        _history = history;
        _configManager = configManager;
        _applicationHost = applicationHost;
        _inbound = inbound;
        _outbound = outbound;
        _versions = versions;
        _inboundWorker = inboundWorker;
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
    /// <returns>The listing.</returns>
    [HttpGet("Link")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PeerLinkResponse> Link([FromQuery] string? serverId)
    {
        var response = new PeerLinkResponse { ServerId = _applicationHost.SystemId, ServerName = _applicationHost.FriendlyName };
        var entry = string.IsNullOrWhiteSpace(serverId)
            ? null
            : _configManager.Configuration.Servers.FirstOrDefault(s => string.Equals(s.ServerId, serverId, StringComparison.OrdinalIgnoreCase));
        if (entry is not null)
        {
            response.Listed = true;
            response.Mode = entry.Mode.ToString();
            response.Enabled = entry.IsEnabled && entry.IsConfigured;
            response.PullsFromYou = entry.Pulls;
            response.SendsToYou = entry.Pushes;
        }

        return Ok(response);
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

        var origin = _configManager.Configuration.Servers.FirstOrDefault(s => s.Pulls && string.Equals(s.ServerId, request.SenderServerId, StringComparison.OrdinalIgnoreCase));
        if (origin is null)
        {
            return Conflict($"server {request.SenderServerId} is not configured on this server as a Pull or Sync server, so its changes cannot be pulled");
        }

        // The whole batch is checked before any of it is stored, so a bad hint never leaves part of
        // the batch applying here while the sender writes the whole batch off as malformed.
        foreach (var hint in request.Items)
        {
            if (string.IsNullOrWhiteSpace(hint.HintId) || string.IsNullOrWhiteSpace(hint.Key))
            {
                return BadRequest("Every hint needs a HintId and a Key");
            }

            if (!string.Equals(hint.OriginServerId, request.SenderServerId, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("A hint's OriginServerId must be the sender; hints are never forwarded");
            }
        }

        var response = new QueueResponse();
        var now = DateTime.UtcNow;
        foreach (var hint in request.Items)
        {
            var result = new QueueResult { HintId = hint.HintId };
            response.Items.Add(result);

            var reason = Unmappable(origin, hint);
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
            return BadRequest("A request body with Items is required");
        }

        var removed = _outbound.Complete(request.Items);
        return Ok(new { Removed = removed });
    }

    /// <summary>Lists this server's inbound queue, so a peer can tell whether work it sent is still held.</summary>
    /// <returns>The inbound rows.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<QueueStatusResponse> Status()
    {
        var completed = _inboundWorker.RecentlyCompleted;
        return Ok(new QueueStatusResponse
        {
            ServerId = _applicationHost.SystemId,
            Inbound = _inbound.GetAll().Select(InboundHintDto.From).ToList(),
            Completed = completed.Select(c => c.HintId).ToList(),
            CompletedHints = completed.ToList()
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

    /// <summary>Removes one inbound row that will never apply. An operator action.</summary>
    /// <param name="id">The row id from <see cref="Status"/>.</param>
    /// <returns>No content, or not found.</returns>
    [HttpDelete("Queue/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult RemoveQueued([FromRoute] long id)
    {
        return _inbound.Delete(id) ? NoContent() : NotFound();
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

        return Ok(_history.Negotiate(request));
    }
}
