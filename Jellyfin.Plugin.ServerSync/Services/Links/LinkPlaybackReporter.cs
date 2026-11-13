using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Tells the source about playback of this server's links that it streams, so it shows there as a live
/// session, lands in its activity log, and updates played state there through Jellyfin's own handling.
/// Playback from the link cache is not reported, since the source has no part in it, and a transcode
/// here shows on the source as a direct stream, since the source only sends the file. Progress goes
/// at most every 30 seconds, and at once when playback pauses or resumes. Reports are sent in the
/// background in the order they happened, so Jellyfin's playback events never wait on the network.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkPlaybackReporter : BackgroundService
{
    /// <summary>The longest gap between two progress reports for one viewer.</summary>
    public static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(30);

    // Reports go one at a time, so a source that stops answering only holds the queue this long per report.
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromSeconds(10);

    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILinkRegistry _links;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<LinkPlaybackReporter> _logger;
    private readonly LinkGrants _grants;
    private readonly Channel<(LinkEntry Link, LinkRoute Route, PeerPlaybackReport Report, int Attempt)> _queue = Channel.CreateBounded<(LinkEntry, LinkRoute, PeerPlaybackReport, int)>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    // The last progress sent per viewer and link, and whether it was paused.
    private readonly ConcurrentDictionary<string, (DateTime At, bool Paused)> _lastProgress = new(StringComparer.Ordinal);

    // The link each session last reported, so a stop that names no item still reaches the source.
    private readonly ConcurrentDictionary<string, LinkEntry> _playing = new(StringComparer.Ordinal);

    // The viewers whose playback the source has been told about, so it also hears their stop.
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);

    private readonly LinkCache? _cache;

    public LinkPlaybackReporter(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        ILinkRegistry links,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        IServerApplicationHost applicationHost,
        LinkGrants grants,
        ILogger<LinkPlaybackReporter> logger,
        LinkCache? cache = null)
    {
        _cache = cache;
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _links = links;
        _configManager = configManager;
        _clientFactory = clientFactory;
        _applicationHost = applicationHost;
        _grants = grants;
        _logger = logger;
    }

    /// <summary>
    /// Whether a progress report is due: the first for a viewer, a pause or resume, or 30 seconds after
    /// the last one sent.
    /// </summary>
    public static bool ProgressDue((DateTime At, bool Paused)? last, bool paused, DateTime now)
        => last is not { } sent || sent.Paused != paused || now - sent.At >= ProgressEvery;

    /// <summary>
    /// How long to wait before each retry of a report that failed. A stop is retried, since no later
    /// report makes up for it and the source would keep the session listed. Start and progress are not:
    /// the next progress report makes up for either. A retry waits outside the queue, so a source that
    /// is down never holds up reports to the others.
    /// </summary>
    public static TimeSpan[] RetryDelays(LinkPlaybackEvent kind)
        => kind == LinkPlaybackEvent.Stop
            ? new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10) }
            : Array.Empty<TimeSpan>();

    /// <summary>
    /// Whether a failed report may succeed if sent again: the source did not answer in time, could not be
    /// reached, or failed itself. A refusal, such as a user the source does not map, is final.
    /// </summary>
    /// <param name="ex">What the send threw.</param>
    /// <returns>True when a retry may help.</returns>
    public static bool Retryable(Exception ex)
        => ex is OperationCanceledException
            || (ex is HttpRequestException http && (http.StatusCode is null || (int)http.StatusCode >= 500));

    /// <summary>
    /// How playback of a link shows on the source. The source only ever sends the file as it is, and
    /// any transcode runs on the server holding the link, so the source shows direct play or direct
    /// stream, never a transcode it is not running.
    /// </summary>
    /// <param name="method">How the viewer here plays it.</param>
    /// <returns>DirectPlay, or DirectStream for anything else.</returns>
    public static string ReportedMethod(string? method)
        => string.Equals(method, nameof(PlayMethod.DirectPlay), StringComparison.OrdinalIgnoreCase)
            ? nameof(PlayMethod.DirectPlay)
            : nameof(PlayMethod.DirectStream);

    /// <summary>
    /// Whether a viewer's playback stays off the source. A viewer whose link is in the cache when they
    /// are first seen plays from this server's disk and asks nothing of the source, so the source is
    /// not told about them. Once the source has been told about a viewer it hears every report through
    /// their stop, so it never keeps a session that was started there.
    /// </summary>
    /// <param name="kind">The event.</param>
    /// <param name="viewer">The viewer and link.</param>
    /// <param name="cached">Whether the link's file is in the cache now.</param>
    /// <returns>True when nothing is sent to the source.</returns>
    public bool PlaysLocally(LinkPlaybackEvent kind, string viewer, Func<bool> cached)
    {
        ArgumentNullException.ThrowIfNull(cached);
        if (kind == LinkPlaybackEvent.Stop)
        {
            return !_reported.TryRemove(viewer, out _);
        }

        if (_reported.ContainsKey(viewer))
        {
            return false;
        }

        if (cached())
        {
            return true;
        }

        _reported[viewer] = 0;
        return false;
    }

    /// <summary>
    /// The link a playback event is reported for, or null when it is not one. Start and progress name
    /// their link and are remembered per session. A player that quits without naming what it played
    /// stops with no item, and the link that session last reported is what stopped, so the source is
    /// told about that one.
    /// </summary>
    /// <param name="kind">The event.</param>
    /// <param name="sessionKey">The viewer's session here.</param>
    /// <param name="named">The link the event's item is, or null.</param>
    /// <param name="namesItem">Whether the event names an item at all.</param>
    /// <returns>The link, or null.</returns>
    public LinkEntry? Track(LinkPlaybackEvent kind, string sessionKey, LinkEntry? named, bool namesItem)
    {
        if (kind != LinkPlaybackEvent.Stop)
        {
            if (named is not null)
            {
                _playing[sessionKey] = named;
            }

            return named;
        }

        return _playing.TryRemove(sessionKey, out var last) && named is null && !namesItem ? last : named;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnStart;
        _sessionManager.PlaybackProgress += OnProgress;
        _sessionManager.PlaybackStopped += OnStopped;
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnStart;
        _sessionManager.PlaybackProgress -= OnProgress;
        _sessionManager.PlaybackStopped -= OnStopped;
        _queue.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var (link, route, report, attempt) in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await SendAsync(link, route, report, attempt, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void OnStart(object? sender, PlaybackProgressEventArgs e) => Enqueue(e, LinkPlaybackEvent.Start);

    private void OnProgress(object? sender, PlaybackProgressEventArgs e) => Enqueue(e, LinkPlaybackEvent.Progress);

    private void OnStopped(object? sender, PlaybackStopEventArgs e) => Enqueue(e, LinkPlaybackEvent.Stop);

    private void Enqueue(PlaybackProgressEventArgs e, LinkPlaybackEvent kind)
    {
        try
        {
            // A film held in several versions reports the film, and names the version being played as
            // its media source. The version is what streams, so it is what the source is told about.
            var sessionKey = e.Session?.Id ?? e.DeviceId ?? string.Empty;
            var path = (Guid.TryParse(e.MediaSourceId, out var version) && version != e.Item?.Id ? _libraryManager.GetItemById(version) : null)?.Path ?? e.Item?.Path;
            var named = path is not null && path.EndsWith(LinkPaths.Extension, StringComparison.OrdinalIgnoreCase) ? _links.Find(path) : null;
            if (Track(kind, sessionKey, named, e.Item is not null) is not { } link)
            {
                return;
            }

            var viewer = sessionKey + "|" + link.LinkId;

            // Playback from the cache is this server's alone, so the source is not told about it.
            if (PlaysLocally(kind, viewer, () => _cache?.Holds(link.LinkId) == true))
            {
                return;
            }

            if (kind == LinkPlaybackEvent.Progress)
            {
                var now = DateTime.UtcNow;
                if (!ProgressDue(_lastProgress.TryGetValue(viewer, out var last) ? last : null, e.IsPaused, now))
                {
                    return;
                }

                _lastProgress[viewer] = (now, e.IsPaused);
            }
            else if (kind == LinkPlaybackEvent.Start)
            {
                _lastProgress[viewer] = (DateTime.UtcNow, e.IsPaused);
            }
            else
            {
                _lastProgress.TryRemove(viewer, out _);
            }

            // Reported to the server the viewer streams from, under its own id of the file.
            var viewerUser = e.Users?.FirstOrDefault();
            var userId = viewerUser?.Id;
            var route = (userId is { } viewerId ? _grants.RouteFor(link.LinkId, viewerId) : null) ?? LinkFallback.PrimaryOf(link);
            _queue.Writer.TryWrite((link, route, new PeerPlaybackReport
            {
                ServerId = _applicationHost.SystemId,
                Event = kind,
                ItemId = route.ItemId,
                UserId = userId?.ToString("N", CultureInfo.InvariantCulture),
                SessionKey = sessionKey,
                PlaySessionId = e.PlaySessionId,
                PositionTicks = e.PlaybackPositionTicks,
                IsPaused = e.IsPaused,
                PlayMethod = ReportedMethod(e.Session?.PlayState?.PlayMethod?.ToString()),
                ClientName = e.ClientName,
                UserName = viewerUser?.Username
            }, 0));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not queue a playback report for a link");
        }
    }

    private async Task SendAsync(LinkEntry link, LinkRoute route, PeerPlaybackReport report, int attempt, CancellationToken cancellationToken)
    {
        if (_configManager.Configuration.FindServer(route.ServerKey) is not { IsEnabled: true, IsConfigured: true } server)
        {
            return;
        }

        report.SourceUserId = Guid.TryParse(report.UserId, out var user) ? LinkAccess.SourceUserFor(server, user) : null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ReportTimeout);
            using var client = _clientFactory.Create(server);
            await client.ReportLinkPlaybackAsync(report, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (Retryable(ex) && RetryDelays(report.Event) is { } delays && attempt < delays.Length)
        {
            _logger.LogDebug(ex, "Could not report the {Event} of {Path} to '{Server}', trying again", report.Event, link.LinkPath, server.DisplayName);
            _ = RequeueAsync((link, route, report, attempt + 1), delays[attempt], cancellationToken);
        }
        catch (Exception ex) when (report.Event == LinkPlaybackEvent.Stop)
        {
            // The source ends the session itself ten minutes after the last report it had.
            _logger.LogWarning("Could not report the stop of {Path} to '{Server}', so it lists the viewer until it gives up on them: {Reason}", link.LinkPath, server.DisplayName, ex.Message);
        }
        catch (Exception ex)
        {
            // A lost start or progress is made good by the next report, and history sync settles played state either way.
            _logger.LogDebug(ex, "Could not report playback of {Path} to '{Server}'", link.LinkPath, server.DisplayName);
        }
    }

    private async Task RequeueAsync((LinkEntry Link, LinkRoute Route, PeerPlaybackReport Report, int Attempt) retry, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            _queue.Writer.TryWrite(retry);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
