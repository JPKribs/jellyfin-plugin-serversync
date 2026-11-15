using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// The link playback reporter must tell a link's source about each viewer's playback, and about nothing that is not a link.
/// </summary>
public sealed class LinkPlaybackReporterTests
{
    private const string LinkPath = "/media/Film.strm";
    private static readonly DateTime Sent = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly LinkEntry Link = new(LinkPath, "/media/Film.mkv", "aaaa", "a");
    private readonly LinkFakeRegistry _registry = new LinkFakeRegistry().Add(Link);
    private readonly LinkGrants _grants = new();
    private readonly InterfaceStub _sessionStub;
    private readonly ISessionManager _sessions;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkPlaybackReporterTests"/> class.
    /// </summary>
    public LinkPlaybackReporterTests() => (_sessions, _sessionStub) = InterfaceStub.Create<ISessionManager>();

    /// <summary>
    /// Progress is reported every 30 seconds, to the second, and at once when nothing was sent yet or the pause state changed.
    /// True: the source sees a pause immediately without being sent every tick.
    /// False: the source would get a report a second per viewer, or show a paused viewer as playing.
    /// </summary>
    /// <param name="secondsSince">How long ago progress was last sent, or null for never.</param>
    /// <param name="wasPaused">Whether the last report said paused.</param>
    /// <param name="paused">Whether playback is paused now.</param>
    /// <param name="due">Whether a report is due.</param>
    [Theory]
    [InlineData(null, false, false, true)]
    [InlineData(0, false, false, false)]
    [InlineData(29, false, false, false)]
    [InlineData(30, false, false, true)]
    [InlineData(1, false, true, true)]
    [InlineData(1, true, false, true)]
    [InlineData(29, true, true, false)]
    public void ProgressDue_EveryThirtySecondsOrOnPause(int? secondsSince, bool wasPaused, bool paused, bool due)
    {
        (DateTime At, bool Paused)? last = secondsSince is { } seconds ? (Sent.AddSeconds(-seconds), wasPaused) : null;

        var result = LinkPlaybackReporter.ProgressDue(last, paused, Sent);

        Assert.Equal(due, result);
    }

    /// <summary>
    /// The viewer and session helpers refuse a missing key or check.
    /// True: the mistake is named where it was made.
    /// False: a null key would be stored and never matched by its stop.
    /// </summary>
    [Fact]
    public void Members_NullArguments_Throw()
    {
        var reporter = Reporter(new FakeClientFactory());

        Assert.Throws<ArgumentNullException>(() => reporter.PlaysLocally(LinkPlaybackEvent.Start, null!, () => false));
        Assert.Throws<ArgumentNullException>(() => reporter.PlaysLocally(LinkPlaybackEvent.Start, "viewer", null!));
        Assert.Throws<ArgumentNullException>(() => reporter.Track(LinkPlaybackEvent.Start, null!, Link, true));
    }

    /// <summary>
    /// A start or progress that names no link is nothing to report, and a later stop for that session reports nothing either.
    /// True: ordinary playback is never reported to another server.
    /// False: a film played from this server's own files would be reported as a link.
    /// </summary>
    [Fact]
    public void Track_NoLinkPlayed_ReportsNothing()
    {
        var reporter = Reporter(new FakeClientFactory());

        var started = reporter.Track(LinkPlaybackEvent.Start, "session", null, true);
        var progressed = reporter.Track(LinkPlaybackEvent.Progress, "session", null, false);
        var stopped = reporter.Track(LinkPlaybackEvent.Stop, "session", null, false);

        Assert.Null(started);
        Assert.Null(progressed);
        Assert.Null(stopped);
    }

    /// <summary>
    /// A stop that names its link reports that link, and the session is then forgotten.
    /// True: one play ends with one stop.
    /// False: a second, itemless stop would be reported again for a play already ended.
    /// </summary>
    [Fact]
    public void Track_StopNamingTheLink_ForgetsTheSession()
    {
        var reporter = Reporter(new FakeClientFactory());
        reporter.Track(LinkPlaybackEvent.Start, "session", Link, true);

        var stopped = reporter.Track(LinkPlaybackEvent.Stop, "session", Link, true);
        var again = reporter.Track(LinkPlaybackEvent.Stop, "session", null, false);

        Assert.Same(Link, stopped);
        Assert.Null(again);
    }

    /// <summary>
    /// Starting listens for playback starts, progress, and stops, and stopping stops listening and ends the service.
    /// True: link playback is reported while the plugin runs and not after.
    /// False: the source would never hear of a viewer, or a stopped service would keep handling Jellyfin's events.
    /// </summary>
    [Fact]
    public async Task StartAsync_ThenStop_SubscribesAndUnsubscribes()
    {
        using var reporter = Reporter(new FakeClientFactory());
        await reporter.StartAsync(CancellationToken.None);

        await reporter.StopAsync(CancellationToken.None);

        var events = _sessionStub.Calls.Select(c => c.Name).ToList();
        Assert.Equal(new[] { "add_PlaybackStart", "add_PlaybackProgress", "add_PlaybackStopped", "remove_PlaybackStart", "remove_PlaybackProgress", "remove_PlaybackStopped" }, events);
        Assert.True(reporter.ExecuteTask!.IsCompleted);
        Assert.False(reporter.ExecuteTask.IsFaulted);
    }

    /// <summary>
    /// A playback start of a link is sent to the server the viewer's grant routes to, naming that server's id of the file, the viewer, and their user there.
    /// True: the source lists the viewer and tracks played state for the right one of its users.
    /// False: the report would go to the wrong server, or name an item or user the source does not know.
    /// </summary>
    [Fact]
    public async Task PlaybackStart_OfALink_IsReportedToTheRoutedServer()
    {
        var viewer = new User("joe", "Default", "Default");
        var sent = new TaskCompletionSource<HttpRequestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHandler(request =>
        {
            sent.TrySetResult(request);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var factory = new LinkStubClientFactory(handler);
        _grants.Grant(Link.LinkId, viewer.Id, new LinkRoute("b", "bbbb"));
        using var reporter = Reporter(factory, viewer);
        await reporter.StartAsync(CancellationToken.None);
        var onStart = (EventHandler<PlaybackProgressEventArgs>)_sessionStub.Calls.Single(c => c.Name == "add_PlaybackStart").Args[0]!;

        onStart(this, new PlaybackProgressEventArgs
        {
            Item = new Movie { Id = Guid.NewGuid(), Path = LinkPath },
            Users = new List<User> { viewer },
            DeviceId = "device-1",
            ClientName = "Jellyfin Web",
            PlaybackPositionTicks = 77,
            PlaySessionId = "play-1"
        });
        var request = await sent.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await reporter.StopAsync(CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var report = body.RootElement;
        Assert.Equal(new[] { "b" }, factory.Created);
        Assert.Equal("/Sync/Peer/Playback", request.RequestUri!.AbsolutePath);
        Assert.Equal("this-server", report.GetProperty("ServerId").GetString());
        Assert.Equal("bbbb", report.GetProperty("ItemId").GetString());
        Assert.Equal(viewer.Id.ToString("N"), report.GetProperty("UserId").GetString());
        Assert.Equal("source-joe", report.GetProperty("SourceUserId").GetString());
        Assert.Equal("device-1", report.GetProperty("SessionKey").GetString());
        Assert.Equal("play-1", report.GetProperty("PlaySessionId").GetString());
        Assert.Equal(77, report.GetProperty("PositionTicks").GetInt64());
        Assert.Equal("DirectStream", report.GetProperty("PlayMethod").GetString());
        Assert.Equal("Jellyfin Web", report.GetProperty("ClientName").GetString());
        Assert.Equal("joe", report.GetProperty("UserName").GetString());
    }

    /// <summary>
    /// Playback of something that is not this server's link, or an event with no item, builds no client and does not throw.
    /// True: only link playback ever leaves this server.
    /// False: every film watched here would be reported to another server, or a bare event would throw into Jellyfin.
    /// </summary>
    /// <param name="path">The played item's path, or null for no item.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("/media/Film.mkv")]
    [InlineData("/media/Users Own.strm")]
    public async Task PlaybackStart_NotALink_ReportsNothing(string? path)
    {
        var factory = new FakeClientFactory();
        using var reporter = Reporter(factory);
        await reporter.StartAsync(CancellationToken.None);
        var onStart = (EventHandler<PlaybackProgressEventArgs>)_sessionStub.Calls.Single(c => c.Name == "add_PlaybackStart").Args[0]!;
        var played = new PlaybackProgressEventArgs { Item = path is null ? null : new Movie { Id = Guid.NewGuid(), Path = path }, DeviceId = "device-1" };

        var failure = Record.Exception(() => onStart(this, played));
        await reporter.StopAsync(CancellationToken.None);

        Assert.Null(failure);
        Assert.Equal(0, factory.Requests);
    }

    private LinkPlaybackReporter Reporter(ISourceServerClientFactory factory, User? viewer = null)
    {
        var config = new PluginConfiguration();
        var server = new SourceServer { Key = "b", Name = "Server B", Url = "http://b:8096", ApiKey = "k", Mode = ServerMode.Pull };
        if (viewer is not null)
        {
            server.UserMappings.Add(new UserMapping { SourceUserId = "source-joe", LocalUserId = viewer.Id.ToString("N") });
        }

        config.Servers.Add(server);
        var (library, _) = InterfaceStub.Create<ILibraryManager>();
        var (host, hostStub) = InterfaceStub.Create<IServerApplicationHost>();
        hostStub.Answer("get_SystemId", _ => "this-server");
        return new LinkPlaybackReporter(_sessions, library, _registry, new FakeConfigManager(config), factory, host, _grants, NullLogger<LinkPlaybackReporter>.Instance);
    }
}
