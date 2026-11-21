using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Streams the file a link stands in for into a response, from the link cache when the file is held
/// there and otherwise from the server its grant is routed to. A server that is down, full, or refuses
/// hands the read to another that holds the file, and a stream that drops partway is picked up where it stopped.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkStreamer
{
    /// <summary>How many times one read is picked up again after its source drops it.</summary>
    public const int MaxResumes = 3;

    /// <summary>The value returned once the response has been written.</summary>
    public const int Written = 0;

    private const int BufferBytes = 81920;

    /// <summary>The most of a refusal's text that is read and logged.</summary>
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
    private readonly LinkCache? _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkStreamer"/> class.
    /// </summary>
    /// <param name="configManager">Reads the plugin configuration.</param>
    /// <param name="clientFactory">Builds clients for other servers.</param>
    /// <param name="applicationHost">Names this server.</param>
    /// <param name="access">Picks the server a link streams from.</param>
    /// <param name="grants">The plays in progress.</param>
    /// <param name="health">Remembers which servers did not answer.</param>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="cache">The link cache a held file plays from.</param>
    public LinkStreamer(
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        IServerApplicationHost applicationHost,
        ILinkAccess access,
        LinkGrants grants,
        LinkHealth health,
        ILogger<LinkStreamer> logger,
        LinkCache cache)
        : this(configManager, clientFactory, applicationHost, access, grants, health, logger, StallTimeout, cache)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkStreamer"/> class.
    /// </summary>
    /// <param name="configManager">Reads the plugin configuration.</param>
    /// <param name="clientFactory">Builds clients for other servers.</param>
    /// <param name="applicationHost">Names this server.</param>
    /// <param name="access">Picks the server a link streams from.</param>
    /// <param name="grants">The plays in progress.</param>
    /// <param name="health">Remembers which servers did not answer.</param>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="stallTimeout">How long a source may send nothing before its stream counts as stalled.</param>
    /// <param name="cache">The link cache a held file plays from, or null to always stream.</param>
    public LinkStreamer(
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        IServerApplicationHost applicationHost,
        ILinkAccess access,
        LinkGrants grants,
        LinkHealth health,
        ILogger<LinkStreamer> logger,
        TimeSpan stallTimeout,
        LinkCache? cache = null)
    {
        _cache = cache;
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
    /// Streams a link for a grant into the response, with the content type taken from the file's name
    /// here and never from what the other server sent.
    /// </summary>
    /// <param name="context">The request to answer.</param>
    /// <param name="link">The link to stream.</param>
    /// <param name="grant">Who the link is read for, and the server it is routed to.</param>
    /// <param name="sourcePath">The file's path for the log, on the source or where its copy would sit here.</param>
    /// <returns><see cref="Written"/> once the response has been written, otherwise the status to answer with.</returns>
    public async Task<int> StreamAsync(HttpContext context, LinkEntry link, LinkGrant grant, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(grant);

        // A cached link plays from here and asks nothing of the source.
        if (_cache?.PathFor(link.LinkId, link.Size) is { } cached)
        {
            try
            {
                await Results.File(cached, MimeTypes.GetMimeType(cached), enableRangeProcessing: true).ExecuteAsync(context).ConfigureAwait(false);
                return Written;
            }
            catch (IOException) when (!context.Response.HasStarted)
            {
                // Dropped from the cache since the lookup, so it streams from the source.
            }
        }

        using var playing = grant.Probe ? null : _grants.Hold(link.LinkId, grant.UserId ?? Guid.Empty);
        var head = HttpMethods.IsHead(context.Request.Method);
        var aborted = context.RequestAborted;
        var range = context.Request.Headers.Range.ToString();
        var route = grant.Route ?? LinkFallback.PrimaryOf(link);
        var clients = new List<IDisposable>();
        HttpResponseMessage? response = null;
        try
        {
            var passedOver = _health.IsDown(route.ServerKey);
            var failure = StatusCodes.Status502BadGateway;

            // A server that just failed to answer is not waited on again.
            if (!passedOver)
            {
                (response, failure) = await OpenAsync(route, grant, head, range, sourcePath, clients, aborted).ConfigureAwait(false);
            }

            // Another server that holds the file stands in, for later reads too.
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
            http.ContentType = MimeTypes.GetMimeType(string.IsNullOrEmpty(link.CopiedPath) ? sourcePath : link.CopiedPath);
            http.Headers["X-Content-Type-Options"] = "nosniff";
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
                    _logger.LogInformation("Stopped a read of the link {Path}: {Reason}", FormatUtilities.SanitizeForLog(link.LinkPath), FormatUtilities.SanitizeForLog(outcome, 300));
                    context.Abort();
                    return Written;
                }

                var serverName = _configManager.Configuration.FindServer(route.ServerKey)?.DisplayName;
                response!.Dispose();
                response = null;

                // A dropped stream is asked for again from where it stopped.
                if (window is { } w && w.Start + sent <= w.End && resumes < MaxResumes)
                {
                    resumes++;
                    var rest = $"bytes={w.Start + sent}-{w.End}";
                    var (again, _) = await OpenAsync(route, grant, false, rest, sourcePath, clients, aborted).ConfigureAwait(false);
                    if (Continues(again, w, sent))
                    {
                        _logger.LogInformation("'{Server}' stopped sending {Path} partway ({Reason}), so the rest was asked for again", FormatUtilities.SanitizeForLog(serverName), FormatUtilities.SanitizeForLog(sourcePath), FormatUtilities.SanitizeForLog(outcome, 300));
                        response = again;
                        continue;
                    }

                    again?.Dispose();
                    if (await StandInAsync(link, grant, route, false, rest, sourcePath, clients, aborted).ConfigureAwait(false) is { } next)
                    {
                        if (Continues(next.Response, w, sent))
                        {
                            _logger.LogInformation("'{Server}' stopped sending {Path} partway ({Reason}), so another server sends the rest", FormatUtilities.SanitizeForLog(serverName), FormatUtilities.SanitizeForLog(sourcePath), FormatUtilities.SanitizeForLog(outcome, 300));
                            (response, route) = next;
                            continue;
                        }

                        next.Response.Dispose();
                    }
                }

                // The response has begun, so only closing the connection can tell the player.
                _logger.LogWarning("'{Server}' stopped sending {Path} partway and the rest could not be had: {Reason}", FormatUtilities.SanitizeForLog(serverName), FormatUtilities.SanitizeForLog(sourcePath), FormatUtilities.SanitizeForLog(outcome, 300));
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

    /// <summary>
    /// Whether a status hands the read to another server, as when one is down, refuses, or lacks the file.
    /// A bad range or request would fail the same way anywhere, so it goes back to the player.
    /// </summary>
    private static bool FailsOver(HttpStatusCode status)
        => status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests || (int)status >= 500;

    /// <summary>The bytes of the file a response carries, by where they start and end, or null when it does not say.</summary>
    private static (long Start, long End, long? Total)? WindowOf(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            return response.Content.Headers.ContentRange is { From: { } from, To: { } to } range ? (from, to, range.Length) : null;
        }

        return response.Content.Headers.ContentLength is > 0 and var length ? (0, length - 1, length) : null;
    }

    /// <summary>Whether a reopened response carries exactly the bytes still owed, of a file the same length as the first.</summary>
    private static bool Continues(HttpResponseMessage? response, (long Start, long End, long? Total) window, long sent)
        => response is { StatusCode: HttpStatusCode.PartialContent }
            && response.Content.Headers.ContentRange is { From: { } from, To: { } to } range
            && from == window.Start + sent
            && to == window.End
            && (window.Total is null || range.Length is null || range.Length == window.Total);

    /// <summary>Reads the start of a refusal's text, up to <see cref="MaxReasonBytes"/>.</summary>
    private static async Task<string> ReadReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxReasonBytes];
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                return Encoding.UTF8.GetString(buffer, 0, read);
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            // The source closed the connection before its reason was sent.
            return "(no reason: the connection closed)";
        }
    }

    /// <summary>
    /// Copies a response's body to the player, and says how it ended and why. Only the wait for the source
    /// is timed, so a player that stops reading, such as while paused, is never taken for a stalled source.
    /// </summary>
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

                    // This server's own media probe reads a little of the file, never all.
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

    /// <summary>How a copy of a response's body ended.</summary>
    private enum CopyEnd
    {
        Sent,
        Dropped,
        Refused
    }

    /// <summary>
    /// Asks the other servers that hold the file, opens it on the first that allows the play, and moves
    /// the grant there, or returns null when none can stand in.
    /// </summary>
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

    /// <summary>
    /// Opens the file on one server, whose client stays open in <paramref name="clients"/> since the response reads through it.
    /// A refusal is logged and returned so the caller can fall back, and null comes with the status to send when the server cannot be used.
    /// </summary>
    private async Task<(HttpResponseMessage? Response, int Failure)> OpenAsync(LinkRoute route, LinkGrant grant, bool head, string? range, string sourcePath, List<IDisposable> clients, CancellationToken aborted)
    {
        var server = _configManager.Configuration.FindServer(route.ServerKey);
        if (server is null || !server.IsEnabled || !server.IsConfigured)
        {
            _logger.LogWarning("Could not stream {Path}: the server it links to is no longer configured or is off", FormatUtilities.SanitizeForLog(sourcePath));
            return (null, StatusCodes.Status503ServiceUnavailable);
        }

        var client = _clientFactory.Create(server);
        clients.Add(client);
        try
        {
            var response = await client.OpenPeerStreamAsync(route.ItemId, _applicationHost.SystemId, grant, LinkAccess.SourceUserFor(server, grant.UserId), range, head, false, aborted).ConfigureAwait(false);
            _health.Answered(server.Key);
            if (!response.IsSuccessStatusCode)
            {
                var reason = await ReadReasonAsync(response, aborted).ConfigureAwait(false);
                _logger.LogWarning("'{Server}' refused to stream {Path} ({Status}): {Reason}", server.DisplayName, FormatUtilities.SanitizeForLog(sourcePath), (int)response.StatusCode, FormatUtilities.SanitizeForLog(reason, 300));
            }

            return (response, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !aborted.IsCancellationRequested)
        {
            _health.Failed(server.Key);
            _logger.LogWarning("Could not reach '{Server}' to stream {Path}: {Reason}", server.DisplayName, FormatUtilities.SanitizeForLog(sourcePath), FormatUtilities.SanitizeForLog(ex.Message, 300));
            return (null, StatusCodes.Status502BadGateway);
        }
    }
}
