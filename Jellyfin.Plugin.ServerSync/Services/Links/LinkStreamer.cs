using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Streams the file a link stands in for into a response, from the server its grant is routed to. A
/// server that is down, full, or refuses hands the read to another that holds the same file. A stream
/// that drops or stalls partway is picked up again where it stopped, from the same server or another,
/// so the player never sees the break.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkStreamer
{
    /// <summary>How many times one read is picked up again after its source drops it.</summary>
    public const int MaxResumes = 3;

    /// <summary>The value returned once the response has been written.</summary>
    public const int Written = 0;

    private const int BufferBytes = 81920;

    // The most of a refusal's text that is read and logged.
    private const int MaxReasonBytes = 2048;

    private static readonly string[] PassedHeaders = { "Content-Range", "Accept-Ranges", "Last-Modified", "ETag" };

    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILinkAccess _access;
    private readonly LinkGrants _grants;
    private readonly LinkHealth _health;
    private readonly ILogger<LinkStreamer> _logger;
    private readonly TimeSpan _stallTimeout;

    public LinkStreamer(
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        IServerApplicationHost applicationHost,
        ILinkAccess access,
        LinkGrants grants,
        LinkHealth health,
        ILogger<LinkStreamer> logger)
        : this(configManager, clientFactory, applicationHost, access, grants, health, logger, StallTimeout)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LinkStreamer"/> class, for tests.</summary>
    public LinkStreamer(
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        IServerApplicationHost applicationHost,
        ILinkAccess access,
        LinkGrants grants,
        LinkHealth health,
        ILogger<LinkStreamer> logger,
        TimeSpan stallTimeout)
    {
        _configManager = configManager;
        _clientFactory = clientFactory;
        _applicationHost = applicationHost;
        _access = access;
        _grants = grants;
        _health = health;
        _logger = logger;
        _stallTimeout = stallTimeout;
    }

    /// <summary>
    /// How long a source may send nothing while the player is waiting for bytes before the stream
    /// counts as stalled. Time the player spends not reading, such as while paused, does not count.
    /// </summary>
    public static TimeSpan StallTimeout { get; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Streams a link for a grant into the response. Returns <see cref="Written"/> once the response
    /// has been written, otherwise the status to answer with.
    /// </summary>
    /// <param name="context">The request to answer.</param>
    /// <param name="link">The link.</param>
    /// <param name="grant">Who the link is read for, and the server it is routed to.</param>
    /// <param name="sourcePath">The file's path on the source, for the log.</param>
    /// <returns><see cref="Written"/>, or a status code.</returns>
    public async Task<int> StreamAsync(HttpContext context, LinkEntry link, LinkGrant grant, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(grant);
        var head = HttpMethods.IsHead(context.Request.Method);
        var aborted = context.RequestAborted;
        var range = context.Request.Headers.Range.ToString();
        var route = grant.Route ?? LinkFallback.PrimaryOf(link);
        var clients = new List<IDisposable>();
        HttpResponseMessage? response = null;
        try
        {
            // A server that did not answer a moment ago is not waited on again while another may hold the file.
            var passedOver = _health.IsDown(route.ServerKey);
            var failure = StatusCodes.Status502BadGateway;
            if (!passedOver)
            {
                (response, failure) = await OpenAsync(route, grant, head, range, sourcePath, clients, aborted).ConfigureAwait(false);
            }

            // The server it streams from is down, full, or refused, so another that holds the same file
            // stands in, and later reads of this playback go to it too.
            if (response is null || FailsOver(response.StatusCode))
            {
                if (await StandInAsync(link, grant, route, head, range, sourcePath, clients, aborted).ConfigureAwait(false) is { } stoodIn)
                {
                    response?.Dispose();
                    (response, route) = stoodIn;
                }
                else if (passedOver)
                {
                    (response, failure) = await OpenAsync(route, grant, head, range, sourcePath, clients, aborted).ConfigureAwait(false);
                }
            }

            if (response is null)
            {
                return failure;
            }

            if (!response.IsSuccessStatusCode)
            {
                return (int)response.StatusCode;
            }

            var http = context.Response;
            http.StatusCode = (int)response.StatusCode;
            http.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            http.ContentLength = response.Content.Headers.ContentLength;
            foreach (var name in PassedHeaders)
            {
                if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
                {
                    http.Headers[name] = string.Join(", ", values);
                }
            }

            if (head)
            {
                return Written;
            }

            var window = WindowOf(response);
            long sent = 0;
            var resumes = 0;
            while (true)
            {
                var (end, outcome) = await CopyAsync(response!, http.Body, link, grant, n => sent += n, aborted).ConfigureAwait(false);
                if (end == CopyEnd.Sent)
                {
                    return Written;
                }

                if (end == CopyEnd.Refused)
                {
                    _logger.LogInformation("Stopped a read of the link {Path}: {Reason}", link.LinkPath, outcome);
                    context.Abort();
                    return Written;
                }

                // The source dropped the stream partway. What is left is asked for again, from where it stopped.
                var serverName = _configManager.Configuration.FindServer(route.ServerKey)?.DisplayName;
                response!.Dispose();
                response = null;
                if (window is { } w && w.Start + sent <= w.End && resumes < MaxResumes)
                {
                    resumes++;
                    var rest = $"bytes={w.Start + sent}-{w.End}";
                    var (again, _) = await OpenAsync(route, grant, false, rest, sourcePath, clients, aborted).ConfigureAwait(false);
                    if (Continues(again, w, sent))
                    {
                        _logger.LogInformation("'{Server}' stopped sending {Path} partway ({Reason}), so the rest was asked for again", serverName, sourcePath, outcome);
                        response = again;
                        continue;
                    }

                    again?.Dispose();
                    if (await StandInAsync(link, grant, route, false, rest, sourcePath, clients, aborted).ConfigureAwait(false) is { } next)
                    {
                        if (Continues(next.Response, w, sent))
                        {
                            _logger.LogInformation("'{Server}' stopped sending {Path} partway ({Reason}), so another server sends the rest", serverName, sourcePath, outcome);
                            (response, route) = next;
                            continue;
                        }

                        next.Response.Dispose();
                    }
                }

                // The response has begun, so the connection is closed to tell the player rather than
                // sending an error it cannot read.
                _logger.LogWarning("'{Server}' stopped sending {Path} partway and the rest could not be had: {Reason}", serverName, sourcePath, outcome);
                context.Abort();
                return Written;
            }
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            // The player stopped or seeked, which closes the request.
            return Written;
        }
        finally
        {
            response?.Dispose();
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }

    // A server that is down, refuses, or no longer has the file hands the read to another that holds it.
    // A bad range or request would fail the same way anywhere, so it goes back to the player.
    private static bool FailsOver(HttpStatusCode status)
        => status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests || (int)status >= 500;

    // The bytes of the file a response carries: where they start and end, or null when it does not say.
    private static (long Start, long End)? WindowOf(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            return response.Content.Headers.ContentRange is { From: { } from, To: { } to } ? (from, to) : null;
        }

        return response.Content.Headers.ContentLength is > 0 and var length ? (0, length - 1) : null;
    }

    // Whether a reopened response carries exactly the bytes still owed.
    private static bool Continues(HttpResponseMessage? response, (long Start, long End) window, long sent)
        => response is { StatusCode: HttpStatusCode.PartialContent }
            && response.Content.Headers.ContentRange is { From: { } from, To: { } to }
            && from == window.Start + sent
            && to == window.End;

    private static async Task<string> ReadReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
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
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            // The source closed the connection before its reason was sent.
            return "(no reason: the connection closed)";
        }
    }

    // Copies a response's body to the player, and says how it ended and why. Only the wait for the
    // source is timed, so a player that stops reading, such as while paused, is never taken for a
    // stalled source.
    private async Task<(CopyEnd End, string? Why)> CopyAsync(HttpResponseMessage response, Stream destination, LinkEntry link, LinkGrant grant, Action<int> onSent, CancellationToken aborted)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        try
        {
            var body = await response.Content.ReadAsStreamAsync(aborted).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                while (true)
                {
                    int read;
                    stall.CancelAfter(_stallTimeout);
                    try
                    {
                        read = await body.ReadAsync(buffer.AsMemory(0, BufferBytes), stall.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
                    {
                        return (CopyEnd.Dropped, $"it sent nothing for {_stallTimeout.TotalSeconds:0} seconds");
                    }

                    stall.CancelAfter(Timeout.InfiniteTimeSpan);
                    if (read == 0)
                    {
                        return (CopyEnd.Sent, null);
                    }

                    // This server's own media probe reads a little of the file, never all of it.
                    if (grant.Probe && !_grants.SpendProbe(link.LinkId, read))
                    {
                        return (CopyEnd.Refused, "this server's media probe read as much as it may");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), aborted).ConfigureAwait(false);
                    onSent(read);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return (CopyEnd.Dropped, ex.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private enum CopyEnd
    {
        Sent,
        Dropped,
        Refused
    }

    // Asks the other servers that hold the file, opens it on the first that allows the play, and moves
    // the grant there. Null when none can stand in.
    private async Task<(HttpResponseMessage Response, LinkRoute Route)?> StandInAsync(LinkEntry link, LinkGrant grant, LinkRoute failed, bool head, string? range, string sourcePath, List<IDisposable> clients, CancellationToken aborted)
    {
        var choice = await _access.ChooseAsync(link, grant.UserId, new[] { failed.ServerKey }, aborted).ConfigureAwait(false);
        if (choice.Route is not { } next)
        {
            return null;
        }

        var (response, _) = await OpenAsync(next, grant, head, range, sourcePath, clients, aborted).ConfigureAwait(false);
        if (response is not { IsSuccessStatusCode: true })
        {
            response?.Dispose();
            return null;
        }

        _grants.Reroute(link.LinkId, grant.UserId ?? Guid.Empty, next);
        return (response, next);
    }

    // Opens the file on one server. A refusal is logged and its answer returned unread past its reason,
    // so the caller can fall back. Null with the status to send when the server cannot be used at all.
    private async Task<(HttpResponseMessage? Response, int Failure)> OpenAsync(LinkRoute route, LinkGrant grant, bool head, string? range, string sourcePath, List<IDisposable> clients, CancellationToken aborted)
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
            var response = await client.OpenPeerStreamAsync(route.ItemId, _applicationHost.SystemId, grant, LinkAccess.SourceUserFor(server, grant.UserId), range, head, aborted).ConfigureAwait(false);
            _health.Answered(server.Key);
            if (!response.IsSuccessStatusCode)
            {
                var reason = await ReadReasonAsync(response, aborted).ConfigureAwait(false);
                _logger.LogWarning("'{Server}' refused to stream {Path} ({Status}): {Reason}", server.DisplayName, sourcePath, (int)response.StatusCode, JPKribs.Jellyfin.Base.FormatUtilities.SanitizeForLog(reason, 300));
            }

            return (response, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !aborted.IsCancellationRequested)
        {
            _health.Failed(server.Key);
            _logger.LogWarning("Could not reach '{Server}' to stream {Path}: {Reason}", server.DisplayName, sourcePath, ex.Message);
            return (null, StatusCodes.Status502BadGateway);
        }
    }
}
