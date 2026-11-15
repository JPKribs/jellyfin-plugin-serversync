using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// Link playback sessions must apply a peer's playback report only for an item and a user this server maps and allows, and clear sessions that go quiet.
/// </summary>
public sealed class LinkPlaybackSessionsTests
{
    private static readonly Guid RemoteUser = Guid.Parse("99999999999999999999999999999999");
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly User _user = new("viewer", "Default", "Default");
    private readonly Movie _movie = new() { Id = Guid.NewGuid(), Path = "/media/Film.mkv" };
    private readonly ISessionManager _manager;
    private readonly InterfaceStub _managerStub;
    private readonly SessionInfo _session;
    private readonly List<SessionInfo> _listed = new();
    private readonly LinkPlaybackSessions _sessions;
    private BaseItem? _item;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkPlaybackSessionsTests"/> class.
    /// </summary>
    public LinkPlaybackSessionsTests()
    {
        _user.SetPermission(PermissionKind.EnableMediaPlayback, true);
        _item = _movie;
        (_manager, _managerStub) = InterfaceStub.Create<ISessionManager>();
        _session = new SessionInfo(_manager, NullLogger.Instance) { Id = "session-here" };
        _managerStub
            .Answer(nameof(ISessionManager.LogSessionActivity), _ => Task.FromResult(_session))
            .Answer(nameof(ISessionManager.OnPlaybackStart), _ => Task.CompletedTask)
            .Answer(nameof(ISessionManager.OnPlaybackProgress), _ => Task.CompletedTask)
            .Answer(nameof(ISessionManager.OnPlaybackStopped), _ => Task.CompletedTask)
            .Answer(nameof(ISessionManager.ReportSessionEnded), _ => ValueTask.CompletedTask)
            .Answer("get_Sessions", _ => _listed);
        var (library, libraryStub) = InterfaceStub.Create<ILibraryManager>();
        libraryStub.Answer(nameof(ILibraryManager.GetItemById), _ => _item);
        var (users, usersStub) = InterfaceStub.Create<IUserManager>();
        usersStub.Answer(nameof(IUserManager.GetUserById), args => (Guid)args[0]! == _user.Id ? _user : null);
        var (host, hostStub) = InterfaceStub.Create<IServerApplicationHost>();
        hostStub.Answer("get_SystemId", _ => "this-server");
        _sessions = new LinkPlaybackSessions(_manager, library, users, new FakeConfigManager { PluginVersion = "9.9.9" }, new FakeClientFactory(), host, NullLogger<LinkSessionController>.Instance);
    }

    /// <summary>
    /// The static helpers and the report refuse a missing server or report.
    /// True: the mistake is named where it was made.
    /// False: a report with no sender would fail with no hint of the cause.
    /// </summary>
    [Fact]
    public async Task Members_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => LinkPlaybackSessions.DeviceIdFor(null!, "key"));
        Assert.Throws<ArgumentNullException>(() => LinkPlaybackSessions.DeviceName(null!, Report()));
        Assert.Throws<ArgumentNullException>(() => LinkPlaybackSessions.DeviceName(Peer(), null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sessions.ApplyAsync(null!, Report(), "1.2.3.4"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sessions.ApplyAsync(Peer(), null!, "1.2.3.4"));
    }

    /// <summary>
    /// A viewer's device id is a fixed prefix and 24 lower case hex characters, the same for the same server and session.
    /// True: every report from one viewer lands on one session here.
    /// False: each report would open a new session, filling the dashboard.
    /// </summary>
    [Fact]
    public void DeviceIdFor_SameViewer_IsStable()
    {
        var first = LinkPlaybackSessions.DeviceIdFor(Peer(), "viewer-1");

        var second = LinkPlaybackSessions.DeviceIdFor(Peer(), "viewer-1");

        Assert.Equal(first, second);
        Assert.Matches("^serversync-link-[0-9a-f]{24}$", first);
    }

    /// <summary>
    /// Names from another server lose their control characters and are held to 64 characters.
    /// True: a peer cannot break the dashboard's session list with a crafted name.
    /// False: a name with line breaks or thousands of characters would be shown as sent.
    /// </summary>
    [Fact]
    public void DeviceName_HostileNames_AreCleaned()
    {
        var report = Report();
        report.ClientName = "Web\r\n\tClient";
        report.UserName = new string('u', 200);

        var name = LinkPlaybackSessions.DeviceName(Peer(), report);

        Assert.Equal("WebClient (" + new string('u', 64) + ")", name);
    }

    /// <summary>
    /// A report from a server that may not link here is refused before anything is looked up.
    /// True: switching Allow Linking off stops that server's reports from moving played state.
    /// False: a server with no right to stream could still mark items played.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_ServerMayNotLink_IsRefused()
    {
        var peer = Peer();
        peer.AllowLinking = false;

        var refusal = await _sessions.ApplyAsync(peer, Report(), "1.2.3.4");

        Assert.Equal("linking is not allowed for Peer here", refusal);
        Assert.Empty(_managerStub.Calls);
    }

    /// <summary>
    /// A report naming an unreadable id or an item this server does not hold is refused.
    /// True: a report can only touch an item that exists here.
    /// False: a malformed id would throw out of the peer route.
    /// </summary>
    /// <param name="itemId">The id the report names, or null for a valid id of nothing.</param>
    [Theory]
    [InlineData("not-an-id")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ApplyAsync_NoSuchItem_IsRefused(string? itemId)
    {
        var report = Report();
        report.ItemId = itemId ?? Guid.NewGuid().ToString("N");
        _item = null;

        var refusal = await _sessions.ApplyAsync(Peer(), report, "1.2.3.4");

        Assert.Equal("no such item here", refusal);
        Assert.Empty(_managerStub.Calls);
    }

    /// <summary>
    /// A report for a user with no mapping here, or mapped to a user who is gone, is refused, whatever user it claims.
    /// True: played state moves only for a user this server chose.
    /// False: a peer could mark items played for any account by naming it.
    /// </summary>
    /// <param name="mapped">Whether a mapping exists, to a user who is gone.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_UserNotMapped_IsRefused(bool mapped)
    {
        var peer = Peer();
        peer.UserMappings[0].LocalUserId = mapped ? Guid.NewGuid().ToString("N") : "unreadable";
        var report = Report();
        report.SourceUserId = _user.Id.ToString("N");

        var refusal = await _sessions.ApplyAsync(peer, report, "1.2.3.4");

        Assert.Equal("the user is not mapped to a user here", refusal);
        Assert.Empty(_managerStub.Calls);
    }

    /// <summary>
    /// A report that names no session is refused before a session is made.
    /// True: every session here belongs to one viewer there.
    /// False: all keyless reports would pile onto one shared session.
    /// </summary>
    /// <param name="key">The session key the report carries.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ApplyAsync_NoSessionKey_IsRefused(string key)
    {
        var report = Report();
        report.SessionKey = key;

        var refusal = await _sessions.ApplyAsync(Peer(), report, "1.2.3.4");

        Assert.Equal("the report names no session", refusal);
        Assert.Empty(_managerStub.Calls);
    }

    /// <summary>
    /// A start report opens a session under the Server Sync client for the mapped user and starts playback of the item.
    /// True: the source's dashboard lists the viewer, and Jellyfin tracks played state for the right user.
    /// False: link playback would be invisible on the source, or counted for the wrong account.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_Start_OpensTheSessionAndStartsPlayback()
    {
        var report = Report();
        report.PositionTicks = 1234;
        report.PlayMethod = "Transcode";

        var refusal = await _sessions.ApplyAsync(Peer(), report, "1.2.3.4");

        var activity = Assert.Single(_managerStub.Calls, c => c.Name == nameof(ISessionManager.LogSessionActivity)).Args;
        var start = (PlaybackStartInfo)Assert.Single(_managerStub.Calls, c => c.Name == nameof(ISessionManager.OnPlaybackStart)).Args[0]!;
        Assert.Null(refusal);
        Assert.Equal(new object?[] { LinkPolicy.ClientName, "9.9.9", LinkPlaybackSessions.DeviceIdFor(Peer(), "viewer-1"), "Jellyfin Web (Joe)", "1.2.3.4", _user }, activity);
        Assert.Equal((_movie.Id, "session-here", _movie.Id.ToString("N"), 1234L, PlayMethod.DirectStream), (start.ItemId, start.SessionId, start.MediaSourceId, start.PositionTicks, start.PlayMethod));
        Assert.IsType<LinkSessionController>(Assert.Single(_session.SessionControllers));
        Assert.Single(_managerStub.Calls, c => c.Name == nameof(ISessionManager.ReportCapabilities));
    }

    /// <summary>
    /// A second report on the same session keeps its one controller and does not report capabilities again.
    /// True: a session is set up once however many reports arrive.
    /// False: every progress report would stack another controller on the session.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_SecondReport_ReusesTheController()
    {
        await _sessions.ApplyAsync(Peer(), Report(), "1.2.3.4");

        await _sessions.ApplyAsync(Peer(), Report(), "1.2.3.4");

        Assert.Single(_session.SessionControllers);
        Assert.Single(_managerStub.Calls, c => c.Name == nameof(ISessionManager.ReportCapabilities));
    }

    /// <summary>
    /// Progress for a session that is not playing the item starts it, and progress for one that is reports progress.
    /// True: a viewer whose start report was lost still shows up and has their position kept.
    /// False: progress with no start would be dropped, and the viewer never listed.
    /// </summary>
    /// <param name="alreadyPlaying">Whether the session is already playing the item.</param>
    /// <param name="expected">The session manager call the report becomes.</param>
    [Theory]
    [InlineData(false, nameof(ISessionManager.OnPlaybackStart))]
    [InlineData(true, nameof(ISessionManager.OnPlaybackProgress))]
    public async Task ApplyAsync_Progress_StartsWhenTheStartWasLost(bool alreadyPlaying, string expected)
    {
        _session.NowPlayingItem = alreadyPlaying ? new BaseItemDto { Id = _movie.Id } : null;
        var report = Report();
        report.Event = LinkPlaybackEvent.Progress;

        var refusal = await _sessions.ApplyAsync(Peer(), report, "1.2.3.4");

        var playback = _managerStub.Calls.Select(c => c.Name).Where(n => n.StartsWith("OnPlayback", StringComparison.Ordinal));
        Assert.Null(refusal);
        Assert.Equal(new[] { expected }, playback);
    }

    /// <summary>
    /// A stop that Jellyfin throws on still counts as applied.
    /// True: the linking server does not resend a stop that can never succeed.
    /// False: one bad stop would be retried and logged for ever.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_StopThatThrows_CountsAsApplied()
    {
        _managerStub.Answer(nameof(ISessionManager.OnPlaybackStopped), _ => throw new InvalidOperationException("no such session"));
        var report = Report();
        report.Event = LinkPlaybackEvent.Stop;

        var refusal = await _sessions.ApplyAsync(Peer(), report, "1.2.3.4");

        Assert.Null(refusal);
        Assert.Single(_managerStub.Calls, c => c.Name == nameof(ISessionManager.OnPlaybackStopped));
    }

    /// <summary>
    /// An event this server does not know is refused by name.
    /// True: a newer peer's event is turned away plainly.
    /// False: an unknown event would be applied as a start or a stop.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_UnknownEvent_IsRefused()
    {
        var report = Report();
        report.Event = (LinkPlaybackEvent)99;

        var refusal = await _sessions.ApplyAsync(Peer(), report, "1.2.3.4");

        Assert.Equal("unknown playback event", refusal);
    }

    /// <summary>
    /// A stale session whose stop throws is still removed, and one whose removal throws is not counted, while the sweep goes on.
    /// True: one broken session never keeps the others listed.
    /// False: a single failing session would end the sweep and leave every stale viewer on the dashboard.
    /// </summary>
    [Fact]
    public async Task SweepAsync_SessionThatFails_DoesNotEndTheSweep()
    {
        _listed.Add(Stale("will-not-stop", playing: true));
        _listed.Add(Stale("will-not-go", playing: false));
        _listed.Add(Stale("goes", playing: false));
        _managerStub.Answer(nameof(ISessionManager.OnPlaybackStopped), _ => throw new InvalidOperationException("stop failed"));
        _managerStub.Answer(nameof(ISessionManager.ReportSessionEnded), args => (string)args[0]! == "will-not-go" ? throw new InvalidOperationException("end failed") : ValueTask.CompletedTask);

        var ended = await _sessions.SweepAsync(Now);

        Assert.Equal(2, ended);
        Assert.Equal(new[] { "will-not-stop", "will-not-go", "goes" }, _managerStub.Calls.Where(c => c.Name == nameof(ISessionManager.ReportSessionEnded)).Select(c => (string)c.Args[0]!));
    }

    /// <summary>
    /// With no sessions at all the sweep removes nothing.
    /// True: an idle server's sweep is a no-op.
    /// False: the sweep would throw every minute on a quiet server.
    /// </summary>
    [Fact]
    public async Task SweepAsync_NoSessions_RemovesNothing()
    {
        var ended = await _sessions.SweepAsync(Now);

        Assert.Equal(0, ended);
    }

    private SessionInfo Stale(string id, bool playing)
    {
        var controller = new LinkSessionController("peer", id, null!, null!, null!, NullLogger.Instance);
        controller.Touch(Now - LinkSessionController.RemoveAfter);
        return new SessionInfo(_manager, NullLogger.Instance)
        {
            Id = id,
            DeviceName = id,
            SessionControllers = new ISessionController[] { controller },
            NowPlayingItem = playing ? new BaseItemDto { Id = _movie.Id } : null
        };
    }

    private PeerPlaybackReport Report() => new()
    {
        ServerId = "peer-id",
        Event = LinkPlaybackEvent.Start,
        ItemId = _movie.Id.ToString("N"),
        UserId = RemoteUser.ToString("N"),
        SessionKey = "viewer-1",
        ClientName = "Jellyfin Web",
        UserName = "Joe"
    };

    private SourceServer Peer() => new()
    {
        Key = "peer",
        Name = "Peer",
        IsEnabled = true,
        Mode = ServerMode.Push,
        AllowLinking = true,
        UserMappings = { new UserMapping { SourceUserId = RemoteUser.ToString("N"), LocalUserId = _user.Id.ToString("N") } }
    };
}
