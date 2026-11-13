using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Links;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Links;

/// <summary>
/// How a link viewer's session on the source comes to an end: the stop report that ends it, and the
/// sweep that ends it when that report never arrives.
/// </summary>
public sealed class LinkSessionEndTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A link session with no report for ten minutes is ended, and its playback stopped first.
    /// True: a viewer whose stop report was lost leaves the source's dashboard on their own.
    /// False: they would stay listed until the source restarts.
    /// </summary>
    [Fact]
    public async Task Sweep_StaleLinkSession_StopsAndEnds()
    {
        var (sessions, manager, stub) = Sessions();
        var playing = Guid.NewGuid();
        var stale = Session(manager, "stale", Link(Now - LinkSessionController.ActiveFor - TimeSpan.FromSeconds(1)));
        stale.NowPlayingItem = new BaseItemDto { Id = playing };
        stale.PlayState = new PlayerStateInfo { MediaSourceId = "v", PositionTicks = 42 };
        stub.Answer("get_" + nameof(ISessionManager.Sessions), _ => new[] { stale });

        Assert.Equal(1, await sessions.SweepAsync(Now));

        var stop = (PlaybackStopInfo)stub.Calls.Single(c => c.Name == nameof(ISessionManager.OnPlaybackStopped)).Args[0]!;
        Assert.Equal(playing, stop.ItemId);
        Assert.Equal("stale", stop.SessionId);
        Assert.Equal(42, stop.PositionTicks);
        Assert.Equal("stale", stub.Calls.Single(c => c.Name == nameof(ISessionManager.ReportSessionEnded)).Args[0]);
    }

    /// <summary>
    /// A link session reported on recently, a viewer's own session, and a session with another
    /// controller are all left alone.
    /// True: only link sessions whose server stopped reporting are ended.
    /// False: someone watching through a link, or on the source itself, would be thrown off it.
    /// </summary>
    [Fact]
    public async Task Sweep_LeavesLiveAndOtherSessions()
    {
        var (sessions, manager, stub) = Sessions();
        var live = Session(manager, "live", Link(Now - TimeSpan.FromMinutes(1)));
        var own = Session(manager, "own");
        var other = Session(manager, "other", Link(Now - TimeSpan.FromHours(1)), new IdleController());
        stub.Answer("get_" + nameof(ISessionManager.Sessions), _ => new[] { live, own, other });

        Assert.Equal(0, await sessions.SweepAsync(Now));
        Assert.DoesNotContain(stub.Calls, c => c.Name is nameof(ISessionManager.ReportSessionEnded) or nameof(ISessionManager.OnPlaybackStopped));
    }

    /// <summary>
    /// A stale session with nothing playing is ended without a stop.
    /// True: a session whose playback Jellyfin already stopped as idle still leaves the list.
    /// False: it would stay listed, or Jellyfin would be sent a stop for nothing.
    /// </summary>
    [Fact]
    public async Task Sweep_StaleIdleSession_EndsWithoutStop()
    {
        var (sessions, manager, stub) = Sessions();
        stub.Answer("get_" + nameof(ISessionManager.Sessions), _ => new[] { Session(manager, "idle", Link(Now - TimeSpan.FromHours(1))) });

        Assert.Equal(1, await sessions.SweepAsync(Now));
        Assert.DoesNotContain(stub.Calls, c => c.Name == nameof(ISessionManager.OnPlaybackStopped));
    }

    /// <summary>
    /// A stop that names no item is reported for the link that session last played, once.
    /// True: a player that quits without saying what it played still ends its session on the source.
    /// False: the stop would be dropped and the viewer would stay listed there.
    /// </summary>
    [Fact]
    public void Reporter_ItemlessStop_ReportsLastLink()
    {
        var reporter = Reporter();
        var link = new LinkEntry("/m/Film.strm", "/m/Film.mkv", "a", "s");
        Assert.Same(link, reporter.Track(LinkPlaybackEvent.Start, "viewer", link, true));
        Assert.Same(link, reporter.Track(LinkPlaybackEvent.Stop, "viewer", null, false));
        Assert.Null(reporter.Track(LinkPlaybackEvent.Stop, "viewer", null, false));
    }

    /// <summary>
    /// A stop for an item that is not a link is never reported as the link played before it, and
    /// another session's stop never takes this session's link.
    /// True: the source only hears about the link that actually stopped.
    /// False: moving on to a real file, or another viewer stopping, would end this viewer's session there.
    /// </summary>
    [Fact]
    public void Reporter_StopForOtherItemOrSession_IsNotTheLink()
    {
        var reporter = Reporter();
        var link = new LinkEntry("/m/Film.strm", "/m/Film.mkv", "a", "s");
        reporter.Track(LinkPlaybackEvent.Progress, "viewer", link, true);
        Assert.Null(reporter.Track(LinkPlaybackEvent.Stop, "someone else", null, false));
        Assert.Null(reporter.Track(LinkPlaybackEvent.Stop, "viewer", null, true));
        Assert.Null(reporter.Track(LinkPlaybackEvent.Stop, "viewer", null, false));
    }

    /// <summary>
    /// A failed stop report is tried again, and a failed start or progress is not.
    /// True: one dropped request does not leave the viewer listed on the source.
    /// False: a lost stop would never be sent, or every progress hiccup would hold up the queue.
    /// </summary>
    [Fact]
    public void Reporter_RetriesStopOnly()
    {
        Assert.NotEmpty(LinkPlaybackReporter.RetryDelays(LinkPlaybackEvent.Stop));
        Assert.Empty(LinkPlaybackReporter.RetryDelays(LinkPlaybackEvent.Start));
        Assert.Empty(LinkPlaybackReporter.RetryDelays(LinkPlaybackEvent.Progress));
    }

    /// <summary>
    /// The source is only ever told a link plays as a direct play or a direct stream.
    /// True: a transcode on the server holding the link never shows as one on the source, which only sends the file.
    /// False: the source's dashboard would claim a transcode it is not running.
    /// </summary>
    [Theory]
    [InlineData("DirectPlay", "DirectPlay")]
    [InlineData("directplay", "DirectPlay")]
    [InlineData("DirectStream", "DirectStream")]
    [InlineData("Transcode", "DirectStream")]
    [InlineData(null, "DirectStream")]
    [InlineData("nonsense", "DirectStream")]
    public void Reporter_ReportedMethod_NeverTranscode(string? method, string expected)
        => Assert.Equal(expected, LinkPlaybackReporter.ReportedMethod(method));

    /// <summary>
    /// A viewer playing a cached link is never reported to the source, start to stop.
    /// True: playback from this server's own disk does not show on the source.
    /// False: the source would list a viewer it sends nothing to.
    /// </summary>
    [Fact]
    public void Reporter_CachedViewer_StaysLocal()
    {
        var reporter = Reporter();
        Assert.True(reporter.PlaysLocally(LinkPlaybackEvent.Start, "v|a", () => true));
        Assert.True(reporter.PlaysLocally(LinkPlaybackEvent.Progress, "v|a", () => true));
        Assert.True(reporter.PlaysLocally(LinkPlaybackEvent.Stop, "v|a", () => true));
    }

    /// <summary>
    /// A viewer the source was told about is reported through their stop, even when the file lands in
    /// the cache partway, and a viewer whose cached file is dropped partway starts being reported.
    /// True: a session started on the source is always ended there, and a stream that moves to the source shows there.
    /// False: the source would keep a session it never hears the end of, or stream to someone it never lists.
    /// </summary>
    [Fact]
    public void Reporter_ReportedViewer_ReportedThroughStop()
    {
        var reporter = Reporter();
        Assert.False(reporter.PlaysLocally(LinkPlaybackEvent.Start, "v|a", () => false));
        Assert.False(reporter.PlaysLocally(LinkPlaybackEvent.Progress, "v|a", () => true));
        Assert.False(reporter.PlaysLocally(LinkPlaybackEvent.Stop, "v|a", () => true));

        Assert.True(reporter.PlaysLocally(LinkPlaybackEvent.Start, "w|a", () => true));
        Assert.False(reporter.PlaysLocally(LinkPlaybackEvent.Progress, "w|a", () => false));
        Assert.False(reporter.PlaysLocally(LinkPlaybackEvent.Stop, "w|a", () => false));
    }

    /// <summary>
    /// A viewer's session on the source is named after their client and their user on the server
    /// holding the link, and a linking server that sends no user name is named instead.
    /// True: the dashboard reads like the viewer's own server, as in Jellyfin Web (Joe).
    /// False: every viewer would show under the linking server's name, or a name from another server could break the line.
    /// </summary>
    [Theory]
    [InlineData("Jellyfin Web", "JPKwibs", "Jellyfin Web (JPKwibs)")]
    [InlineData(null, "JPKwibs", "Parker (JPKwibs)")]
    [InlineData("Jellyfin Web", null, "Parker (Server Sync, Jellyfin Web)")]
    [InlineData(" ", "", "Parker (Server Sync)")]
    [InlineData("Swiftfin\n", "Jo\u0007e", "Swiftfin (Joe)")]
    public void Sessions_DeviceName_IsClientAndUser(string? client, string? user, string expected)
        => Assert.Equal(expected, LinkPlaybackSessions.DeviceName(
            new Models.Configuration.SourceServer { Name = "Parker" },
            new PeerPlaybackReport { ClientName = client is null ? null : System.Text.RegularExpressions.Regex.Unescape(client), UserName = user is null ? null : System.Text.RegularExpressions.Regex.Unescape(user) }));

    /// <summary>
    /// A report is sent again only when the source did not answer or failed itself, never when it refused.
    /// True: a source that is briefly down still hears the stop, and a refusal is not repeated.
    /// False: a refused stop would be sent three times, or a stop lost to a timeout never sent again.
    /// </summary>
    [Fact]
    public void Reporter_RetriesOnlyWhatMaySucceed()
    {
        Assert.True(LinkPlaybackReporter.Retryable(new TaskCanceledException()));
        Assert.True(LinkPlaybackReporter.Retryable(new System.Net.Http.HttpRequestException("down")));
        Assert.True(LinkPlaybackReporter.Retryable(new System.Net.Http.HttpRequestException("failed", null, System.Net.HttpStatusCode.BadGateway)));
        Assert.False(LinkPlaybackReporter.Retryable(new System.Net.Http.HttpRequestException("refused", null, System.Net.HttpStatusCode.Forbidden)));
        Assert.False(LinkPlaybackReporter.Retryable(new System.Net.Http.HttpRequestException("bad", null, System.Net.HttpStatusCode.BadRequest)));
        Assert.False(LinkPlaybackReporter.Retryable(new InvalidOperationException()));
    }

    private static (LinkPlaybackSessions Sessions, ISessionManager Manager, InterfaceStub Stub) Sessions()
    {
        var (manager, stub) = InterfaceStub.Create<ISessionManager>();
        stub.Answer(nameof(ISessionManager.OnPlaybackStopped), _ => Task.CompletedTask);
        var sessions = new LinkPlaybackSessions(manager, null!, null!, null!, null!, null!, NullLogger<LinkSessionController>.Instance);
        return (sessions, manager, stub);
    }

    private static SessionInfo Session(ISessionManager manager, string id, params ISessionController[] controllers)
        => new(manager, NullLogger.Instance) { Id = id, DeviceName = id, SessionControllers = controllers };

    private static LinkSessionController Link(DateTime lastReport)
    {
        var controller = new LinkSessionController("peer", "viewer", null!, null!, null!, NullLogger.Instance);
        controller.Touch(lastReport);
        return controller;
    }

    private static LinkPlaybackReporter Reporter()
        => new(null!, null!, null!, null!, null!, null!, null!, NullLogger<LinkPlaybackReporter>.Instance);

    private sealed class IdleController : ISessionController
    {
        public bool IsSessionActive => false;

        public bool SupportsMediaControl => false;

        public Task SendMessage<T>(SessionMessageType name, Guid messageId, T data, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
