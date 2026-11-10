using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Tells the source about playback of this server's links, so it shows there as a live session, lands
/// in its activity log, and updates played state there through Jellyfin's own handling. Progress goes
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
    private readonly ILinkRegistry _links;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<LinkPlaybackReporter> _logger;
    private readonly LinkGrants _grants;
    private readonly Channel<(LinkEntry Link, LinkRoute Route, PeerPlaybackReport Report)> _queue = Channel.CreateBounded<(LinkEntry, LinkRoute, PeerPlaybackReport)>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    // The last progress sent per viewer and link, and whether it was paused.
    private readonly ConcurrentDictionary<string, (DateTime At, bool Paused)> _lastProgress = new(StringComparer.Ordinal);

    public LinkPlaybackReporter(
        ISessionManager sessionManager,
        ILinkRegistry links,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        IServerApplicationHost applicationHost,
        LinkGrants grants,
        ILogger<LinkPlaybackReporter> logger)
    {
        _sessionManager = sessionManager;
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
            await foreach (var (link, route, report) in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await SendAsync(link, route, report, stoppingToken).ConfigureAwait(false);
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
            var path = e.Item?.Path;
            if (path is null || !path.EndsWith(LinkPaths.Extension, StringComparison.OrdinalIgnoreCase) || _links.Find(path) is not { } link)
            {
                return;
            }

            var sessionKey = e.Session?.Id ?? e.DeviceId ?? string.Empty;
            var viewer = sessionKey + "|" + link.LinkId;
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
            var userId = e.Users?.FirstOrDefault()?.Id;
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
                PlayMethod = e.Session?.PlayState?.PlayMethod?.ToString(),
                ClientName = e.ClientName
            }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not queue a playback report for a link");
        }
    }

    private async Task SendAsync(LinkEntry link, LinkRoute route, PeerPlaybackReport report, CancellationToken cancellationToken)
    {
        if (_configManager.Configuration.FindServer(route.ServerKey) is not { IsEnabled: true, IsConfigured: true } server)
        {
            return;
        }

        try
        {
            report.SourceUserId = Guid.TryParse(report.UserId, out var user) ? LinkAccess.SourceUserFor(server, user) : null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ReportTimeout);
            using var client = _clientFactory.Create(server);
            await client.ReportLinkPlaybackAsync(report, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A lost report is made good by the next one, and history sync settles played state either way.
            _logger.LogDebug(ex, "Could not report playback of {Path} to '{Server}'", link.LinkPath, server.DisplayName);
        }
    }
}
