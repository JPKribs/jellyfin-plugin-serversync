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
[Route("ServerSync/Peer")]
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
    [RequireSameVersion]
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
}
