using System.Net.Mime;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

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

    public PeerPairController(IPluginConfigurationManager configManager, PeerPairingStore pairings, ILogger<PeerPairController> logger)
    {
        _configManager = configManager;
        _pairings = pairings;
        _logger = logger;
    }

    [HttpPost("Pair")]
    [RequireCompatibleProtocol]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<object> Pair([FromBody] PairRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ServerId) || !PeerPairingStore.WellFormed(request.Secret))
        {
            return BadRequest("A request body with ServerId and a Secret of 32 to 256 letters, digits, - or _ is required");
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
