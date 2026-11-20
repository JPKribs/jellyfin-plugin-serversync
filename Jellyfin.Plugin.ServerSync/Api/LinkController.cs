using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Api;

/// <summary>
/// Serves the URL inside every link this server wrote, streaming the file from its source for the user who asked to play it.
/// Jellyfin's own callers send no token, so the route answers this server alone and only with the secret this server derives for the link.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route(LinkFiles.Route)]
public class LinkController : ControllerBase
{
    /// <summary>
    /// The headers that mark a request as passed through a proxy, which Jellyfin's own callers never send.
    /// Jellyfin leaves the X-Original headers behind when it trusts a proxy, so those are refused too.
    /// </summary>
    private static readonly string[] ProxyHeaders = { "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Real-IP", "Forwarded", "Via", "X-Original-For", "X-Original-Host", "X-Original-Proto" };

    private readonly LinkFiles _files;
    private readonly LinkGrants _grants;
    private readonly ContentSyncTableManager _table;
    private readonly LinkStreamer _streamer;
    private readonly ILogger<LinkController> _logger;

    /// <summary>Initializes a new instance of the <see cref="LinkController"/> class.</summary>
    /// <param name="files">Reads and writes link files.</param>
    /// <param name="grants">Records which user a link streams for.</param>
    /// <param name="table">Reads the Content sync table.</param>
    /// <param name="streamer">Streams a file from its source.</param>
    /// <param name="logger">Writes log entries.</param>
    public LinkController(LinkFiles files, LinkGrants grants, ContentSyncTableManager table, LinkStreamer streamer, ILogger<LinkController> logger)
    {
        _files = files;
        _grants = grants;
        _table = table;
        _streamer = streamer;
        _logger = logger;
    }

    /// <summary>
    /// Whether a request comes from this server itself, from and to a loopback address with no sign of a proxy.
    /// </summary>
    /// <param name="context">The request whose addresses and headers are checked.</param>
    /// <returns><c>true</c> when the request comes from this server itself.</returns>
    public static bool FromThisServer(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var remote = context.Connection.RemoteIpAddress;
        var local = context.Connection.LocalIpAddress;
        if (remote is null || local is null)
        {
            return false;
        }

        if (remote.IsIPv4MappedToIPv6)
        {
            remote = remote.MapToIPv4();
        }

        if (local.IsIPv4MappedToIPv6)
        {
            local = local.MapToIPv4();
        }

        return IPAddress.IsLoopback(remote)
            && IPAddress.IsLoopback(local)
            && !ProxyHeaders.Any(h => context.Request.Headers.ContainsKey(h));
    }

    /// <summary>Streams the file a link stands in for, from the source that holds it.</summary>
    /// <param name="linkId">The link's ID, which is the item's ID on the source.</param>
    /// <param name="key">The secret the link carries.</param>
    /// <returns>An empty result once the stream is written, or the status that refuses it.</returns>
    [HttpGet("{linkId}")]
    [HttpHead("{linkId}")]
    public async Task<IActionResult> Get([FromRoute] string linkId, [FromQuery(Name = "k")] string? key)
    {
        if (!FromThisServer(HttpContext))
        {
            _logger.LogWarning("Refused a link request from {Remote}: links only answer this server itself", HttpContext.Connection.RemoteIpAddress);
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!_files.SecretMatches(linkId, key))
        {
            _logger.LogWarning("Refused a link request for {LinkId}: the secret does not match", JPKribs.Jellyfin.Base.FormatUtilities.SanitizeForLog(linkId, 64));
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var row = _table.GetBySourceItemId(linkId);
        if (row is null || !row.StorageMode.IsLink())
        {
            return NotFound();
        }

        // Only a user who asked to play the link, or the probe, may read.
        if (_grants.Use(linkId) is not { } grant)
        {
            _logger.LogWarning("Refused a read of the link {Path}: nobody here asked to play it", JPKribs.Jellyfin.Base.FormatUtilities.SanitizeForLog(row.LocalPath));
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var link = new LinkEntry(row.LocalPath ?? string.Empty, LinkPaths.CopiedPathOf(row) ?? string.Empty, row.SourceItemId, row.ServerKey, row.SourceSize);
        var status = await _streamer.StreamAsync(HttpContext, link, grant, row.SourcePath).ConfigureAwait(false);
        return status == LinkStreamer.Written ? new EmptyResult() : StatusCode(status);
    }
}
