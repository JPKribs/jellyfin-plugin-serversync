using System;
using System.Net.Mime;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// Operator actions on a server entry beyond saving the configuration. Removing an entry leaves rows in
/// every sync table that carry its key with nothing to pull from, so the Servers tab asks for them to
/// go when the entry is removed. Files already downloaded are never touched.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("ServerSync/Servers")]
[Produces(MediaTypeNames.Application.Json)]
public class ServersController : ControllerBase
{
    private readonly ContentSyncTableManager _content;
    private readonly HistorySyncTableManager _history;
    private readonly MetadataSyncTableManager _metadata;
    private readonly PeopleSyncTableManager _people;
    private readonly UserSyncTableManager _users;
    private readonly OutboundHintStore _outbound;
    private readonly InboundHintStore _inbound;
    private readonly Services.Peer.PeerPairingStore _pairings;
    private readonly ILogger<ServersController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServersController"/> class.
    /// </summary>
    /// <param name="content">The content table.</param>
    /// <param name="history">The history table.</param>
    /// <param name="metadata">The metadata table.</param>
    /// <param name="people">The people table.</param>
    /// <param name="users">The user table.</param>
    /// <param name="outbound">The outbound hint store.</param>
    /// <param name="inbound">The inbound hint store.</param>
    /// <param name="pairings">The pairing secrets.</param>
    /// <param name="logger">Logger.</param>
    public ServersController(
        ContentSyncTableManager content,
        HistorySyncTableManager history,
        MetadataSyncTableManager metadata,
        PeopleSyncTableManager people,
        UserSyncTableManager users,
        OutboundHintStore outbound,
        InboundHintStore inbound,
        Services.Peer.PeerPairingStore pairings,
        ILogger<ServersController> logger)
    {
        _pairings = pairings;
        _inbound = inbound;
        _content = content;
        _history = history;
        _metadata = metadata;
        _people = people;
        _users = users;
        _outbound = outbound;
        _logger = logger;
    }

    /// <summary>Removes every sync row and queued hint that came from one server entry.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="serverId">The removed server's id, so hints it sent and that wait here go too.</param>
    /// <returns>How many rows were removed per table.</returns>
    [HttpDelete("{key}/Rows")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<ServerRowsRemoved> ForgetRows([FromRoute] string key, [FromQuery] string? serverId)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return BadRequest("A server key is required");
        }

        var removed = new ServerRowsRemoved
        {
            Content = _content.DeleteByServerKey(key),
            History = _history.DeleteByServerKey(key),
            Metadata = _metadata.DeleteByServerKey(key),
            People = _people.DeleteByServerKey(key),
            Users = _users.DeleteByServerKey(key),
            Hints = _outbound.DeleteForPeer(key) + (string.IsNullOrWhiteSpace(serverId) ? 0 : _inbound.DeleteForOrigin(serverId))
        };
        _pairings.Remove(key);
        _logger.LogInformation(
            "Forgot server {Key}: {Content} content, {History} history, {Metadata} metadata, {People} people, {Users} user row(s), {Hints} queued hint(s)",
            key, removed.Content, removed.History, removed.Metadata, removed.People, removed.Users, removed.Hints);
        return Ok(removed);
    }
}

/// <summary>What was removed when a server entry was forgotten.</summary>
public class ServerRowsRemoved
{
    /// <summary>Gets or sets the content rows removed.</summary>
    public int Content { get; set; }

    /// <summary>Gets or sets the history rows removed.</summary>
    public int History { get; set; }

    /// <summary>Gets or sets the metadata rows removed.</summary>
    public int Metadata { get; set; }

    /// <summary>Gets or sets the people rows removed.</summary>
    public int People { get; set; }

    /// <summary>Gets or sets the user rows removed.</summary>
    public int Users { get; set; }

    /// <summary>Gets or sets the queued hints removed.</summary>
    public int Hints { get; set; }
}
