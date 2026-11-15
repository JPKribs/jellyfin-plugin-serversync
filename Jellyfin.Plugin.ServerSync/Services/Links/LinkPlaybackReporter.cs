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
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using JPKribs.Jellyfin.Base;
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
/// session and updates played state there, while playback from the link cache is not reported. Reports go
/// in the background in order, progress at most every 30 seconds, so Jellyfin's playback events never wait.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkPlaybackReporter : BackgroundService
{
    /// <summary>The longest gap between two progress reports for one viewer.</summary>
    public static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(30);

    /// <summary>How long one report may hold the queue, since reports go one at a time.</summary>
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

    /// <summary>The last progress sent per viewer and link, and whether it was paused.</summary>
    private readonly ConcurrentDictionary<string, (DateTime At, bool Paused)> _lastProgress = new(StringComparer.Ordinal);

    /// <summary>The link each session last reported, so a stop that names no item still reaches the source.</summary>
    private readonly ConcurrentDictionary<string, LinkEntry> _playing = new(StringComparer.Ordinal);

    /// <summary>The viewers whose playback the source has been told about, so it also hears their stop.</summary>
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);

    private readonly LinkCache? _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkPlaybackReporter"/> class.
    /// </summary>
    /// <param name="sessionManager">Raises the playback events that are reported.</param>
    /// <param name="libraryManager">Finds the version an event names.</param>
    /// <param name="links">Tells which paths are this server's links.</param>
    /// <param name="configManager">Reads the plugin configuration.</param>
    /// <param name="clientFactory">Builds clients for other servers.</param>
    /// <param name="applicationHost">Names this server.</param>
    /// <param name="grants">Tells which server each viewer streams from.</param>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="cache">The link cache, whose playback is not reported, or null.</param>
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
    /// Whether a progress report is due, as the first for a viewer, a pause or resume, or 30 seconds after
    /// the last one sent.
    /// </summary>
    /// <param name="last">The last report sent for the viewer, or null when none was.</param>
    /// <param name="paused">Whether the viewer is paused now.</param>
    /// <param name="now">The time to measure the gap since the last report from.</param>
    /// <returns><c>true</c> when a report is due.</returns>
    public static bool ProgressDue((DateTime At, bool Paused)? last, bool paused, DateTime now)
        => last is not { } sent || sent.Paused != paused || now - sent.At >= ProgressEvery;

    /// <summary>
    /// How long to wait before each retry of a report that failed. Only a stop is retried, since a lost
    /// stop leaves the session listed on the source and the next progress report makes up for anything else.
    /// </summary>
    /// <param name="kind">The kind of report that failed.</param>
    /// <returns>The wait before each retry, or none.</returns>
    public static TimeSpan[] RetryDelays(LinkPlaybackEvent kind)
        => kind == LinkPlaybackEvent.Stop
            ? new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10) }
            : Array.Empty<TimeSpan>();

    /// <summary>
    /// Whether a failed report may succeed if sent again, as when the source did not answer in time, could
    /// not be reached, or failed itself. A refusal, such as a user the source does not map, is final.
    /// </summary>
    /// <param name="ex">What the send threw.</param>
    /// <returns><c>true</c> when a retry may help.</returns>
    public static bool Retryable(Exception ex)
        => ex is OperationCanceledException
            || (ex is HttpRequestException http && (http.StatusCode is null || (int)http.StatusCode >= 500));

    /// <summary>
    /// How playback of a link shows on the source. The source only sends the file as it is and any transcode
    /// runs on the linking server, so the source shows direct play or direct stream.
    /// </summary>
    /// <param name="method">How the viewer here plays it.</param>
    /// <returns>DirectPlay, or DirectStream for anything else.</returns>
    public static string ReportedMethod(string? method)
        => string.Equals(method, nameof(PlayMethod.DirectPlay), StringComparison.OrdinalIgnoreCase)
            ? nameof(PlayMethod.DirectPlay)
            : nameof(PlayMethod.DirectStream);

    /// <summary>
    /// Whether a viewer's playback stays off the source, which is so when their link is in the link cache
    /// when they are first seen. Once the source has been told about a viewer it hears every report through
    /// their stop, so it never keeps a session that was started there.
    /// </summary>
    /// <param name="kind">The kind of playback event.</param>
    /// <param name="viewer">The key of the viewer's session and link.</param>
    /// <param name="cached">Tells whether the link's file is in the link cache.</param>
    /// <returns><c>true</c> when nothing is sent to the source.</returns>
    public bool PlaysLocally(LinkPlaybackEvent kind, string viewer, Func<bool> cached)
    {
        ArgumentNullException.ThrowIfNull(viewer);
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
    /// their link and are remembered per session, so a stop that names no item is reported for the link
    /// that session last played.
    /// </summary>
    /// <param name="kind">The kind of playback event.</param>
    /// <param name="sessionKey">The viewer's session here.</param>
    /// <param name="named">The link the event's item is, or null.</param>
    /// <param name="namesItem">Whether the event names an item at all.</param>
    /// <returns>The link, or null.</returns>
    public LinkEntry? Track(LinkPlaybackEvent kind, string sessionKey, LinkEntry? named, bool namesItem)
    {
        ArgumentNullException.ThrowIfNull(sessionKey);
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

    /// <summary>
    /// Queues a report of one playback event when it is for a link the source streams. A film held in several
    /// versions names the version played as its media source, and that version is what the source is told about.
    /// </summary>
    private void Enqueue(PlaybackProgressEventArgs e, LinkPlaybackEvent kind)
    {
        // This runs in Jellyfin's playback events, which must never see a throw.
        try
        {
            var sessionKey = e.Session?.Id ?? e.DeviceId ?? string.Empty;
            var path = (Guid.TryParse(e.MediaSourceId, out var version) && version != e.Item?.Id ? _libraryManager.GetItemById(version) : null)?.Path ?? e.Item?.Path;
            var named = path is not null && path.EndsWith(LinkPaths.Extension, StringComparison.OrdinalIgnoreCase) ? _links.Find(path) : null;
            if (Track(kind, sessionKey, named, e.Item is not null) is not { } link)
            {
                return;
            }

            var viewer = sessionKey + "|" + link.LinkId;

            // Playback from the link cache is this server's alone, so it is not reported.
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

    /// <summary>Sends one report to the server the viewer streams from, and queues a retry of a stop that may yet succeed.</summary>
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
            _logger.LogDebug(ex, "Could not report the {Event} of {Path} to '{Server}', trying again", report.Event, FormatUtilities.SanitizeForLog(link.LinkPath), server.DisplayName);
            _ = RequeueAsync((link, route, report, attempt + 1), delays[attempt], cancellationToken);
        }
        catch (Exception ex) when (report.Event == LinkPlaybackEvent.Stop)
        {
            // The source stops the playback itself ten minutes after the last report it had.
            _logger.LogWarning("Could not report the stop of {Path} to '{Server}', so it lists the viewer until it gives up on them: {Reason}", FormatUtilities.SanitizeForLog(link.LinkPath), server.DisplayName, FormatUtilities.SanitizeForLog(ex.Message, 300));
        }
        catch (Exception ex)
        {
            // A lost start or progress is made good by the next report.
            _logger.LogDebug(ex, "Could not report playback of {Path} to '{Server}'", FormatUtilities.SanitizeForLog(link.LinkPath), server.DisplayName);
        }
    }

    /// <summary>Puts a report back on the queue after a wait, outside the queue so other reports are not held up.</summary>
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
