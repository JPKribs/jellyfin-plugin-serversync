using System.Net.Mime;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// Lets a server that lists this one ask to pair, so the two share a secret that proves who sends each call.
/// </summary>
[ApiController]
[Authorize]
[Route("Sync/Peer")]
[PeerCallerGuard]
[RequestSizeLimit(64 * 1024)]
[Produces(MediaTypeNames.Application.Json)]
public class PeerPairController : ControllerBase
{
    private readonly IPluginConfigurationManager _configManager;
    private readonly PeerPairingStore _pairings;
    private readonly ILogger<PeerPairController> _logger;

    /// <summary>Initializes a new instance of the <see cref="PeerPairController"/> class.</summary>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="pairings">The store of pairing secrets.</param>
    /// <param name="logger">Writes log entries.</param>
    public PeerPairController(IPluginConfigurationManager configManager, PeerPairingStore pairings, ILogger<PeerPairController> logger)
    {
        _configManager = configManager;
        _pairings = pairings;
        _logger = logger;
    }

    /// <summary>Takes the secret a server offers and records it for that server.</summary>
    /// <param name="request">The sender's id and the secret.</param>
    /// <returns>The result of the pairing.</returns>
    [HttpPost("Pair")]
    [RequireCompatibleProtocol]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<object> Pair([FromBody] PairRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ServerId) || !PeerPairingStore.WellFormed(request.Secret))
        {
            return BadRequest("A request body with ServerId and a Secret of 32 to 256 letters, digits, - or _ is required.");
        }

        var entry = _configManager.Configuration.FindServerById(request.ServerId);
        if (entry is null)
        {
            return Conflict($"server {request.ServerId} is not configured on this server, so it cannot pair");
        }

        // A secret is only taken while this server is asking that entry for one. Any whitelisted caller
        // can name an entry, and taken at any time its secret would replace the real one and cut this
        // server off from that entry until the two paired again.
        if (!_pairings.Expecting(entry.Key))
        {
            _logger.LogWarning("Refused a pairing secret for '{Peer}' that this server did not ask for", entry.DisplayName);
            return Conflict("this server is not asking that server to pair right now. Check the link from this server to pair");
        }

        _pairings.SetOutbound(entry.Key, request.Secret);
        _logger.LogInformation("Paired with '{Peer}': it issued the secret this server presents to it", entry.DisplayName);
        return Ok(new { Paired = true });
    }
}
