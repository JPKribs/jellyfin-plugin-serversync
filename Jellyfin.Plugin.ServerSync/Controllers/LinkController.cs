using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Links;
using MediaBrowser.Controller;
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
/// user who asked to play the link, whom the source checks again.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route(LinkFiles.Route)]
public class LinkController : ControllerBase
{
    private static readonly string[] PassedHeaders = { "Content-Range", "Accept-Ranges", "Last-Modified", "ETag" };

    // A request that passed through a proxy carries one of these. Jellyfin's own callers never do, and a
    // reverse proxy on this machine would otherwise look like this server itself.
    private static readonly string[] ProxyHeaders = { "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Real-IP", "Forwarded", "Via" };

    // The most of a refusal's text that is read and logged.
    private const int MaxReasonBytes = 2048;

    private readonly LinkFiles _files;
    private readonly LinkGrants _grants;
    private readonly ContentSyncTableManager _table;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILinkAccess _access;
    private readonly ILogger<LinkController> _logger;

    public LinkController(
        LinkFiles files,
        LinkGrants grants,
        ContentSyncTableManager table,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        IServerApplicationHost applicationHost,
        ILinkAccess access,
        ILogger<LinkController> logger)
    {
        _files = files;
        _grants = grants;
        _table = table;
        _configManager = configManager;
        _clientFactory = clientFactory;
        _applicationHost = applicationHost;
        _access = access;
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

    private static async Task<string> ReadReasonAsync(HttpResponseMessage response, System.Threading.CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxReasonBytes];
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or HttpRequestException)
        {
            // The source closed the connection before its reason was sent.
            return "(no reason: the connection closed)";
        }
    }

    // A server that is down, refuses, or no longer has the file hands the read to another that holds it.
    // A bad range or request would fail the same way anywhere, so it goes back to the player.
    private static bool FailsOver(HttpStatusCode status)
        => status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests || (int)status >= 500;

    // Opens the file on one server. A refusal is logged and its answer returned unread past its reason,
    // so the caller can fall back. Null with the status to send when the server cannot be used at all.
    private async Task<(HttpResponseMessage? Response, int Failure)> OpenAsync(LinkRoute route, LinkGrant grant, bool head, string sourcePath, List<IDisposable> clients, System.Threading.CancellationToken aborted)
    {
        var server = _configManager.Configuration.FindServer(route.ServerKey);
        if (server is null || !server.IsEnabled || !server.IsConfigured)
        {
            _logger.LogWarning("Could not stream {Path}: the server it links to is no longer configured or is off", sourcePath);
            return (null, StatusCodes.Status503ServiceUnavailable);
        }

        // Kept open until the stream is sent, since the response reads through it.
        var client = _clientFactory.Create(server);
        clients.Add(client);
        try
        {
            var response = await client.OpenPeerStreamAsync(route.ItemId, _applicationHost.SystemId, grant, LinkAccess.SourceUserFor(server, grant.UserId), Request.Headers.Range.ToString(), head, aborted).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var reason = await ReadReasonAsync(response, aborted).ConfigureAwait(false);
                _logger.LogWarning("'{Server}' refused to stream {Path} ({Status}): {Reason}", server.DisplayName, sourcePath, (int)response.StatusCode, JPKribs.Jellyfin.Base.FormatUtilities.SanitizeForLog(reason, 300));
            }

            return (response, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException or TaskCanceledException && !aborted.IsCancellationRequested)
        {
            _logger.LogWarning("Could not reach '{Server}' to stream {Path}: {Reason}", server.DisplayName, sourcePath, ex.Message);
            return (null, StatusCodes.Status502BadGateway);
        }
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

        var head = HttpMethods.IsHead(Request.Method);
        var aborted = HttpContext.RequestAborted;
        var link = new LinkEntry(row.LocalPath ?? string.Empty, LinkPaths.CopiedPathOf(row) ?? string.Empty, row.SourceItemId, row.ServerKey, row.SourceSize);
        var route = grant.Route ?? LinkFallback.PrimaryOf(link);
        var clients = new List<IDisposable>();
        try
        {
            var (response, failure) = await OpenAsync(route, grant, head, row.SourcePath, clients, aborted).ConfigureAwait(false);

            // The server it streams from is down, full, or refused, so another that holds the same file
            // stands in, and later reads of this playback go to it too.
            if (response is null || FailsOver(response.StatusCode))
            {
                var choice = await _access.ChooseAsync(link, grant.UserId, new[] { route.ServerKey }, aborted).ConfigureAwait(false);
                if (choice.Route is { } next)
                {
                    var (nextResponse, nextFailure) = await OpenAsync(next, grant, head, row.SourcePath, clients, aborted).ConfigureAwait(false);
                    if (nextResponse is { IsSuccessStatusCode: true })
                    {
                        response?.Dispose();
                        response = nextResponse;
                        route = next;
                        _grants.Reroute(linkId, grant.UserId ?? Guid.Empty, next);
                    }
                    else
                    {
                        nextResponse?.Dispose();
                        failure = response is null ? nextFailure : failure;
                    }
                }
            }

            if (response is null)
            {
                return StatusCode(failure);
            }

            var serverName = _configManager.Configuration.FindServer(route.ServerKey)?.DisplayName;
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    return StatusCode((int)response.StatusCode);
                }

                Response.StatusCode = (int)response.StatusCode;
                Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
                Response.ContentLength = response.Content.Headers.ContentLength;
                foreach (var name in PassedHeaders)
                {
                    if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
                    {
                        Response.Headers[name] = string.Join(", ", values);
                    }
                }

                if (!head)
                {
                    // Closing the source's stream early drains what is left of it, which can fail too, so the
                    // whole read is guarded, not only the copy.
                    try
                    {
                        var body = await response.Content.ReadAsStreamAsync(aborted).ConfigureAwait(false);
                        await using (body.ConfigureAwait(false))
                        {
                            await body.CopyToAsync(Response.Body, 81920, aborted).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (aborted.IsCancellationRequested)
                    {
                        // The player stopped or seeked, which closes the request.
                    }
                    catch (Exception ex) when (ex is System.IO.IOException or HttpRequestException)
                    {
                        // The source dropped the stream partway. The response has begun, so the connection
                        // is closed to tell the player rather than sending an error it cannot read.
                        _logger.LogWarning("'{Server}' stopped sending {Path} partway: {Reason}", serverName, row.SourcePath, ex.Message);
                        HttpContext.Abort();
                    }
                }

                return new EmptyResult();
            }
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }
}
