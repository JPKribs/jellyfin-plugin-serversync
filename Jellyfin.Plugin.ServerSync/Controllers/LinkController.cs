using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// The URL inside every link this server wrote. Jellyfin's own ffmpeg and its static stream proxy call
/// it while playing a link, and they send no Jellyfin token, so it cannot require one. Instead it
/// answers this server alone, and only with the secret that only this server can derive for the link.
/// It streams the file from the source with this server's own credentials, which never leave it, for the
/// user who asked to play the link, whom the source checks again. Jellyfin reads a link without saying
/// who for, so when two users play one link at once this route reads for the one seen most recently.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route(LinkFiles.Route)]
public class LinkController : ControllerBase
{
    // A request that passed through a proxy carries one of these. Jellyfin's own callers never do, and a
    // reverse proxy on this machine would otherwise look like this server itself.
    private static readonly string[] ProxyHeaders = { "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Real-IP", "Forwarded", "Via" };

    private readonly LinkFiles _files;
    private readonly LinkGrants _grants;
    private readonly ContentSyncTableManager _table;
    private readonly LinkStreamer _streamer;
    private readonly ILogger<LinkController> _logger;

    public LinkController(LinkFiles files, LinkGrants grants, ContentSyncTableManager table, LinkStreamer streamer, ILogger<LinkController> logger)
    {
        _files = files;
        _grants = grants;
        _table = table;
        _streamer = streamer;
        _logger = logger;
    }

    /// <summary>
    /// Whether a request comes from this server itself: from and to a loopback address, with nothing to
    /// say it passed through a proxy on the way.
    /// </summary>
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
    /// <param name="linkId">The link's id.</param>
    /// <param name="key">The link's secret.</param>
    /// <returns>Nothing. The answer is written directly.</returns>
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

        // Only a user who asked to play the link, or this server's own media probe, may read it.
        if (_grants.Use(linkId) is not { } grant)
        {
            _logger.LogWarning("Refused a read of the link {Path}: nobody here asked to play it", row.LocalPath);
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var link = new LinkEntry(row.LocalPath ?? string.Empty, LinkPaths.CopiedPathOf(row) ?? string.Empty, row.SourceItemId, row.ServerKey, row.SourceSize);
        var status = await _streamer.StreamAsync(HttpContext, link, grant, row.SourcePath).ConfigureAwait(false);
        return status == LinkStreamer.Written ? new EmptyResult() : StatusCode(status);
    }
}
