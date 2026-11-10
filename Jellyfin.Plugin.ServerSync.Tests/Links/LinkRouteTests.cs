using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Controllers;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Links;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Links;

/// <summary>
/// The routes and filter that make a link play: the filter on Jellyfin's playback routes, the loopback
/// proxy's refusals, and reading a peer's links.
/// </summary>
public sealed class LinkRouteTests : IDisposable
{
    private const string LinkUrl = "http://127.0.0.1:8096/Sync/Link/abc?k=secret";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "serversync-linkroutes-" + Guid.NewGuid().ToString("N"));

    public LinkRouteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// PlaybackInfo for a link loses the loopback URL, so clients play through this server.
    /// True: a client never tries to open 127.0.0.1 on its own device.
    /// False: direct play of a link would fail on every client but the server itself.
    /// </summary>
    [Fact]
    public async Task Filter_Link_ClearsLoopbackSource()
    {
        var source = new MediaSourceInfo { Path = LinkUrl, IsRemote = true, Protocol = MediaProtocol.Http };
        await RunResult(Filter("/m/Movie.strm", isLink: true), "MediaInfo", source);

        Assert.False(source.IsRemote);
        Assert.Equal(MediaProtocol.File, source.Protocol);
        Assert.Null(source.Path);
    }

    /// <summary>
    /// A .strm that is not one of this server's links is left exactly as Jellyfin built it.
    /// True: a user's own .strm plays as it always did.
    /// False: the filter would rewrite sources it does not own.
    /// </summary>
    [Fact]
    public async Task Filter_UsersOwnStrm_IsUntouched()
    {
        const string Elsewhere = "http://example.com/stream.mkv";
        var source = new MediaSourceInfo { Path = Elsewhere, IsRemote = true, Protocol = MediaProtocol.Http };
        await RunResult(Filter("/m/Movie.strm", isLink: false), "MediaInfo", source);

        Assert.True(source.IsRemote);
        Assert.Equal(Elsewhere, source.Path);
    }

    /// <summary>
    /// A failure while checking for a link passes the response through unchanged.
    /// True: a bug in link handling can never break normal playback.
    /// False: a lookup failure would fail every PlaybackInfo request.
    /// </summary>
    [Fact]
    public async Task Filter_LookupThrows_PassesThrough()
    {
        var (library, stub) = InterfaceStub.Create<ILibraryManager>();
        stub.Answer(nameof(ILibraryManager.GetItemById), _ => throw new InvalidOperationException("boom"));
        var filter = new LinkPlaybackFilter(library, new NoLinks(), new LinkGrants(), new Access(null), NullLogger<LinkPlaybackFilter>.Instance);
        var source = new MediaSourceInfo { Path = "/m/Movie.mkv", Protocol = MediaProtocol.File };

        var ran = await RunResult(filter, "MediaInfo", source);

        Assert.True(ran);
        Assert.Equal("/m/Movie.mkv", source.Path);
    }

    /// <summary>
    /// Universal audio for a link never redirects the client to the link's URL.
    /// True: audio links stream through this server.
    /// False: the client would be sent to a loopback address it cannot reach.
    /// </summary>
    [Fact]
    public async Task Filter_UniversalAudioLink_TurnsOffRedirection()
    {
        var arguments = new Dictionary<string, object?>();
        var context = new ActionExecutingContext(Action("UniversalAudio", Guid.NewGuid()), new List<IFilterMetadata>(), arguments, controller: new object());

        await Filter("/m/Song.strm", isLink: true).OnActionExecutionAsync(context, () => Task.FromResult<ActionExecutedContext>(null!));

        Assert.Equal(false, arguments["enableRedirection"]);
    }

    /// <summary>
    /// The link route refuses any caller that is not this server, and a wrong secret.
    /// True: only Jellyfin's own ffmpeg and stream proxy on this machine can use a link.
    /// False: anyone who learns a link's id could stream the file through this server.
    /// </summary>
    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("127.0.0.1", false)]
    public async Task Proxy_RefusesOtherCallersAndWrongSecret(string remote, bool rightSecret)
    {
        var files = Files();
        var url = files.UrlFor("abc");
        var key = rightSecret ? url[(url.IndexOf("?k=", StringComparison.Ordinal) + 3)..] : "wrong";
        var controller = new LinkController(files, new LinkGrants(), null!, null!, null!, null!, new Access(null), NullLogger<LinkController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse(remote);

        var result = await controller.Get("abc", key);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    /// <summary>
    /// Items read from a peer take the copy path for the peer's links, and nothing else changes.
    /// True: history and metadata match a peer's link by the file it stands in for, and content skips it.
    /// False: a peer's link would match nothing here, or a .strm the peer did not list would be renamed.
    /// </summary>
    [Fact]
    public async Task Client_PeerLinks_TakeTheCopyPath()
    {
        var linked = Guid.NewGuid();
        var own = Guid.NewGuid();
        var handler = new PeerHandler(
            $"{{\"Items\":[{{\"Id\":\"{linked:D}\",\"Path\":\"/p/Movie.strm\"}},{{\"Id\":\"{own:D}\",\"Path\":\"/p/Own.strm\"}}],\"TotalRecordCount\":2}}",
            "{\"Links\":[{\"LinkPath\":\"/p/Movie.strm\",\"CopiedPath\":\"/p/Movie.mkv\"}]}");
        using var client = new SourceServerClient(NullLogger<SourceServerClient>.Instance, new HttpClient(handler), "http://peer", "k", "test", "0");

        var items = await client.GetItemsByIdsAsync(new[] { linked, own }, new[] { Jellyfin.Sdk.Generated.Models.ItemFields.Path });

        Assert.Equal("/p/Movie.mkv", items.Find(i => i.Id == linked)!.Path);
        Assert.Equal("/p/Own.strm", items.Find(i => i.Id == own)!.Path);
        Assert.True(client.IsPeerLink(linked));
        Assert.False(client.IsPeerLink(own));
        Assert.Equal(1, handler.LinkCalls);
    }

    /// <summary>
    /// A peer whose items include no .strm is never asked for its links.
    /// True: a peer without links, or without Server Sync, costs no extra request.
    /// False: every scan of every server would make a call that only link holders need.
    /// </summary>
    [Fact]
    public async Task Client_NoStrm_NeverAsksForLinks()
    {
        var id = Guid.NewGuid();
        var handler = new PeerHandler($"{{\"Items\":[{{\"Id\":\"{id:D}\",\"Path\":\"/p/Movie.mkv\"}}],\"TotalRecordCount\":1}}", "{}");
        using var client = new SourceServerClient(NullLogger<SourceServerClient>.Instance, new HttpClient(handler), "http://peer", "k", "test", "0");

        await client.GetItemsByIdsAsync(new[] { id }, new[] { Jellyfin.Sdk.Generated.Models.ItemFields.Path });

        Assert.Equal(0, handler.LinkCalls);
    }

    /// <summary>
    /// Asking to play a link the source refuses stops at PlaybackInfo with the source's reason, and records no grant.
    /// True: a refused user sees why when they press play, and the link stays closed to them.
    /// False: playback would start and fail partway, or the user could stream anyway.
    /// </summary>
    [Fact]
    public async Task Filter_PlayRefusedBySource_StopsWithReason()
    {
        var grants = new LinkGrants();
        var (context, ran) = await RunAction(Filter("/m/Movie.strm", isLink: true, grants, new Access("your account may not play media")), "MediaInfo", Guid.NewGuid());

        Assert.False(ran);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Contains("may not play media", (string)result.Value!, StringComparison.Ordinal);
        Assert.Null(grants.Use("abc"));
    }

    /// <summary>
    /// Asking to play a link the source allows records the user for the link route.
    /// True: the link route streams as the user who pressed play.
    /// False: the link route would refuse every allowed play.
    /// </summary>
    [Fact]
    public async Task Filter_PlayAllowed_GrantsTheUser()
    {
        var grants = new LinkGrants();
        var user = Guid.NewGuid();
        var (_, ran) = await RunAction(Filter("/m/Movie.strm", isLink: true, grants), "MediaInfo", user);

        Assert.True(ran);
        Assert.Equal(user, grants.Use("abc")!.UserId);
    }

    /// <summary>
    /// Playing anything that is not a link never asks a source.
    /// True: normal playback costs nothing and never waits on another server.
    /// False: every play would make a network call.
    /// </summary>
    [Fact]
    public async Task Filter_NotALink_NeverAsks()
    {
        var access = new Access("no");
        var (context, ran) = await RunAction(Filter("/m/Movie.mkv", isLink: false, access: access), "MediaInfo", Guid.NewGuid());

        Assert.True(ran);
        Assert.Null(context.Result);
        Assert.Equal(0, access.Calls);
    }

    /// <summary>
    /// An item response carrying a link's URL in its media sources loses it.
    /// True: no route shows a user the link's secret.
    /// False: a user could read the secret from an item's details.
    /// </summary>
    [Fact]
    public async Task Filter_ItemResponse_LosesLinkUrl()
    {
        var link = new MediaSourceInfo { Path = LinkUrl, IsRemote = true, Protocol = MediaProtocol.Http };
        var plain = new MediaSourceInfo { Path = "/m/Other.mkv", Protocol = MediaProtocol.File };
        var page = new MediaBrowser.Model.Querying.QueryResult<BaseItemDto>(new[] { new BaseItemDto { MediaSources = new[] { link } }, new BaseItemDto { MediaSources = new[] { plain } } });
        var context = new ResultExecutingContext(Action("Items"), new List<IFilterMetadata>(), new ObjectResult(page), controller: new object());

        await Filter("/m/x.mkv", isLink: false).OnResultExecutionAsync(context, () => Task.FromResult<ResultExecutedContext>(null!));

        Assert.Null(link.Path);
        Assert.Equal("/m/Other.mkv", plain.Path);
    }

    /// <summary>
    /// A grant nobody uses expires in two minutes. Once used it lasts while playback goes on. A probe
    /// grant never outlives its own window.
    /// True: a link is only readable around a real play or probe.
    /// False: a link would stay open long after anyone asked, or close mid-film during a pause.
    /// </summary>
    [Fact]
    public void Grants_ExpireUnlessInUse()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var grants = new LinkGrants(() => now);
        var user = Guid.NewGuid();

        grants.Grant("unused", user);
        grants.Grant("used", user);
        grants.GrantProbe("probe");
        Assert.NotNull(grants.Use("used"));
        Assert.NotNull(grants.Use("probe"));

        now += TimeSpan.FromMinutes(3);
        Assert.Null(grants.Use("unused"));
        Assert.Equal(user, grants.Use("used")!.UserId);

        now += TimeSpan.FromMinutes(58);
        Assert.Null(grants.Use("probe"));
        Assert.NotNull(grants.Use("used"));
    }

    /// <summary>
    /// A stream request only keeps alive a grant its own user holds. It never makes one, and never
    /// takes over another user's.
    /// True: a user the source refused cannot stream a link by riding on someone else's play.
    /// False: any user could read a link as whoever pressed play last.
    /// </summary>
    [Fact]
    public void Grants_StreamOnlyRefreshesItsOwn()
    {
        var grants = new LinkGrants();
        var allowed = Guid.NewGuid();
        var refused = Guid.NewGuid();
        grants.Grant("link", allowed);

        Assert.False(grants.Refresh("link", refused));
        Assert.True(grants.Refresh("link", allowed));
        Assert.Equal(allowed, grants.Use("link")!.UserId);
    }

    /// <summary>
    /// A stream on a link that nobody started through PlaybackInfo is refused before Jellyfin opens it.
    /// True: the link route only ever reads for a user the source allowed.
    /// False: a crafted stream request would read the link under another user's grant.
    /// </summary>
    [Fact]
    public async Task Filter_StreamWithoutGrant_IsRefused()
    {
        var (context, ran) = await RunAction(Filter("/m/Movie.strm", isLink: true), "Videos", Guid.NewGuid(), "GetVideoStream");

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(context.Result).StatusCode);
    }

    /// <summary>
    /// Jellyfin's routes that hand out an item's own file refuse a link, since that file is the .strm and its secret.
    /// True: no user can read a link's URL through a download.
    /// False: any user who can see the item could read the secret.
    /// </summary>
    [Theory]
    [InlineData("GetFile")]
    [InlineData("GetDownload")]
    public async Task Filter_FileRoutes_RefuseLinks(string action)
    {
        var (context, ran) = await RunAction(Filter("/m/Movie.strm", isLink: true), "Library", Guid.NewGuid(), action);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(context.Result).StatusCode);
    }

    /// <summary>
    /// The link route only answers a request from and to this machine with no sign of a proxy.
    /// True: a reverse proxy on the same host cannot carry outside requests to a link.
    /// False: with Jellyfin's default of no known proxies, every proxied request would look local.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1", null, true)]
    [InlineData("::ffff:127.0.0.1", "::ffff:127.0.0.1", null, true)]
    [InlineData("127.0.0.1", "127.0.0.1", "X-Forwarded-For", false)]
    [InlineData("127.0.0.1", "127.0.0.1", "Via", false)]
    [InlineData("127.0.0.1", "192.168.1.5", null, false)]
    [InlineData("192.168.1.20", "127.0.0.1", null, false)]
    public void Proxy_FromThisServer(string remote, string local, string? header, bool expected)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        http.Connection.LocalIpAddress = IPAddress.Parse(local);
        if (header is not null)
        {
            http.Request.Headers[header] = "203.0.113.9";
        }

        Assert.Equal(expected, LinkController.FromThisServer(http));
    }

    /// <summary>
    /// A stream counts while a request is open and for a short while after, and only once per user and item.
    /// True: limits see a film being played as one stream through its pauses and seeks.
    /// False: a limit would trip on a seek, or let a second film start in a buffering gap.
    /// </summary>
    [Fact]
    public void Sessions_CountOncePerUserAndItem_AndLinger()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var sessions = new LinkSessions(() => now);
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();

        var first = sessions.Open("peer", user, item);
        sessions.Open("peer", user, item).Dispose();
        Assert.Equal(1, sessions.CountForPeer("PEER"));
        Assert.Equal(1, sessions.CountForUser(user));

        first.Dispose();
        now += TimeSpan.FromSeconds(60);
        Assert.True(sessions.IsActive("peer", user, item));
        now += TimeSpan.FromSeconds(60);
        Assert.False(sessions.IsActive("peer", user, item));
        Assert.Equal(0, sessions.CountForPeer("peer"));
    }

    /// <summary>
    /// The source's own mapping decides who streams. Without one it trusts the caller's mapping, and
    /// otherwise falls back to the configured user, or no one.
    /// True: an administrator on the source controls which of its users each remote user streams as.
    /// False: a remote user could stream as anyone, or a mapped user could be refused.
    /// </summary>
    [Fact]
    public void Policy_UserFor_PrefersTheSourcesOwnMapping()
    {
        var remote = Guid.NewGuid();
        var mapped = Guid.NewGuid();
        var claimed = Guid.NewGuid();
        var fallback = Guid.NewGuid();
        var peer = new SourceServer();

        Assert.Equal(claimed, LinkPolicy.UserFor(peer, remote.ToString(), claimed.ToString()));

        peer.LinkFallbackUserId = fallback.ToString();
        Assert.Equal(fallback, LinkPolicy.UserFor(peer, remote.ToString(), claimed.ToString()));

        peer.UserMappings.Add(new UserMapping { SourceUserId = remote.ToString(), LocalUserId = mapped.ToString(), IsEnabled = true });
        Assert.Equal(mapped, LinkPolicy.UserFor(peer, remote.ToString(), claimed.ToString()));
        Assert.Equal(fallback, LinkPolicy.UserFor(peer, Guid.NewGuid().ToString(), claimed.ToString()));

        peer.LinkFallbackUserId = string.Empty;
        Assert.Null(LinkPolicy.UserFor(peer, Guid.NewGuid().ToString(), claimed.ToString()));
    }

    /// <summary>
    /// Progress goes to the source on the first report, on a pause or resume, and every 30 seconds, never more.
    /// True: the source's session stays alive and current without a report for every tick.
    /// False: the source would be flooded, or would stop the session as idle.
    /// </summary>
    [Fact]
    public void Reporter_ProgressDue_ThrottlesButFollowsPause()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(LinkPlaybackReporter.ProgressDue(null, false, now));
        Assert.False(LinkPlaybackReporter.ProgressDue((now, false), false, now.AddSeconds(10)));
        Assert.True(LinkPlaybackReporter.ProgressDue((now, false), true, now.AddSeconds(10)));
        Assert.True(LinkPlaybackReporter.ProgressDue((now, false), false, now.AddSeconds(30)));
    }

    /// <summary>
    /// Each viewer on the server holding the link is its own device on the source, and stays the same device.
    /// True: two viewers show as two sessions, and one viewer's reports land on one session.
    /// False: viewers would share a session, or each report would make a new one.
    /// </summary>
    [Fact]
    public void Sessions_DeviceIdPerViewer()
    {
        var peer = new SourceServer();
        Assert.Equal(LinkPlaybackSessions.DeviceIdFor(peer, "a"), LinkPlaybackSessions.DeviceIdFor(peer, "a"));
        Assert.NotEqual(LinkPlaybackSessions.DeviceIdFor(peer, "a"), LinkPlaybackSessions.DeviceIdFor(peer, "b"));
        Assert.NotEqual(LinkPlaybackSessions.DeviceIdFor(peer, "a"), LinkPlaybackSessions.DeviceIdFor(new SourceServer(), "a"));
    }

    /// <summary>
    /// The source's dashboard passes on stop, pause, seek, and messages, and nothing else.
    /// True: pressing Stop on the source reaches the viewer on the server holding the link.
    /// False: commands would be lost, or anything the dashboard sends would reach the viewer.
    /// </summary>
    [Fact]
    public void Controller_PassesPlaystateAndMessagesOnly()
    {
        Assert.Null(LinkSessionController.RequestFor(MediaBrowser.Model.Session.SessionMessageType.Playstate, new MediaBrowser.Model.Session.PlaystateRequest { Command = MediaBrowser.Model.Session.PlaystateCommand.Stop }));
        var stop = LinkSessionController.RequestFor(MediaBrowser.Model.Session.SessionMessageType.Playstate, new MediaBrowser.Model.Session.PlaystateRequest { Command = MediaBrowser.Model.Session.PlaystateCommand.Seek, SeekPositionTicks = 42, ControllingUserId = "admin" });
        Assert.Equal("Seek", stop!.Command);
        Assert.Equal(42, stop.SeekPositionTicks);

        var message = new MediaBrowser.Model.Session.GeneralCommand { Name = MediaBrowser.Model.Session.GeneralCommandType.DisplayMessage };
        message.Arguments["Header"] = "Hi";
        message.Arguments["Text"] = "Stopping soon";
        var sent = LinkSessionController.RequestFor(MediaBrowser.Model.Session.SessionMessageType.GeneralCommand, message);
        Assert.Null(sent!.Command);
        Assert.Equal("Stopping soon", sent.Text);

        var other = new MediaBrowser.Model.Session.GeneralCommand { Name = MediaBrowser.Model.Session.GeneralCommandType.GoHome };
        Assert.Null(LinkSessionController.RequestFor(MediaBrowser.Model.Session.SessionMessageType.GeneralCommand, other));
        Assert.Null(LinkSessionController.RequestFor(MediaBrowser.Model.Session.SessionMessageType.UserDataChanged, new object()));
    }

    /// <summary>
    /// An audio link is refused on Jellyfin's audio routes even for a user the source allowed, since
    /// Jellyfin would serve the .strm's text.
    /// True: no route hands out an audio link's secret.
    /// False: universal audio or the audio stream would send the player the link's URL.
    /// </summary>
    [Theory]
    [InlineData("Audio", "GetAudioStream")]
    [InlineData("UniversalAudio", "GetUniversalAudioStream")]
    public async Task Filter_AudioLink_IsRefused(string controller, string action)
    {
        var (library, stub) = InterfaceStub.Create<ILibraryManager>();
        stub.Answer(nameof(ILibraryManager.GetItemById), _ => new MediaBrowser.Controller.Entities.Audio.Audio { Path = "/m/Song.strm" });
        var grants = new LinkGrants();
        var user = Guid.NewGuid();
        grants.Grant("abc", user);
        var filter = new LinkPlaybackFilter(library, new OneLink("/m/Song.strm"), grants, new Access(null), NullLogger<LinkPlaybackFilter>.Instance);

        var (context, ran) = await RunAction(filter, controller, user, action);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(context.Result).StatusCode);
    }

    /// <summary>
    /// Subtitles and fonts inside a linked file are extracted by reading it through the link, so their
    /// routes need a user the source allowed, found by whatever name the route gives the item.
    /// True: no one reads a link through subtitle extraction under another viewer's grant.
    /// False: a user who never pressed play could have the link read for them.
    /// </summary>
    [Theory]
    [InlineData("Subtitle", "GetSubtitle", "routeItemId", false, false)]
    [InlineData("Subtitle", "GetSubtitle", "routeItemId", true, true)]
    [InlineData("Subtitle", "GetSubtitlePlaylist", "itemId", false, false)]
    [InlineData("VideoAttachments", "GetAttachment", "videoId", false, false)]
    [InlineData("Subtitle", "SearchRemoteSubtitles", "itemId", false, true)]
    public async Task Filter_SubtitleAndAttachmentRoutes(string controller, string action, string routeName, bool granted, bool expectRun)
    {
        var grants = new LinkGrants();
        var user = Guid.NewGuid();
        if (granted)
        {
            grants.Grant("abc", user);
        }

        var filter = Filter("/m/Movie.strm", isLink: true, grants);
        var route = new RouteData();
        route.Values[routeName] = Guid.NewGuid().ToString("N");
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", user.ToString()) }, "test")) };
        var context = new ActionExecutingContext(
            new ActionContext(http, route, new ControllerActionDescriptor { ControllerName = controller, ActionName = action }),
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object());
        var ran = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.Equal(expectRun, ran);
    }

    /// <summary>
    /// A new link is probed only once it has settled, its result is checked, and it is probed again until
    /// it has media info, up to three times. A user's own .strm is never probed.
    /// True: a scan saving over the first probe does not leave a link without media info.
    /// False: a link added during a busy scan stays without runtime or codecs until someone plays it.
    /// </summary>
    [Fact]
    public void Probe_WaitsChecksAndRetries()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var link = new Movie { Id = Guid.NewGuid(), Path = "/m/Movie.strm" };
        var own = new Movie { Id = Guid.NewGuid(), Path = "/m/Own.strm" };
        var (library, libraryStub) = InterfaceStub.Create<ILibraryManager>();
        libraryStub.Answer(nameof(ILibraryManager.GetItemById), args => (Guid)args[0]! == link.Id ? link : own);
        var (providers, providerStub) = InterfaceStub.Create<MediaBrowser.Controller.Providers.IProviderManager>();
        var grants = new LinkGrants(() => now);
        var probe = new LinkProbe(library, providers, null!, new OneLink("/m/Movie.strm"), grants, NullLogger<LinkProbe>.Instance, () => now);
        int Probes() => providerStub.Calls.Count(c => c.Name == nameof(MediaBrowser.Controller.Providers.IProviderManager.QueueRefresh));

        probe.Track(link);
        probe.Track(own);
        Assert.Equal(1, probe.Waiting);

        probe.Sweep();
        Assert.Equal(0, Probes());

        now += LinkProbe.Settle;
        probe.Sweep();
        Assert.Equal(1, Probes());
        Assert.True(grants.Use("abc")!.Probe);

        now += LinkProbe.CheckAfter;
        probe.Sweep();
        Assert.Equal(2, Probes());

        link.RunTimeTicks = 20_000_000;
        now += LinkProbe.CheckAfter;
        probe.Sweep();
        Assert.Equal(2, Probes());
        Assert.Equal(0, probe.Waiting);
    }

    /// <summary>
    /// A link that never gets media info is given up after three probes.
    /// True: a broken link costs three probes, not one every two minutes forever.
    /// False: an unreachable source would be probed endlessly.
    /// </summary>
    [Fact]
    public void Probe_GivesUpAfterThreeTries()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var link = new Movie { Id = Guid.NewGuid(), Path = "/m/Movie.strm" };
        var (library, libraryStub) = InterfaceStub.Create<ILibraryManager>();
        libraryStub.Answer(nameof(ILibraryManager.GetItemById), _ => link);
        var (providers, providerStub) = InterfaceStub.Create<MediaBrowser.Controller.Providers.IProviderManager>();
        var probe = new LinkProbe(library, providers, null!, new OneLink("/m/Movie.strm"), new LinkGrants(() => now), NullLogger<LinkProbe>.Instance, () => now);

        probe.Track(link);
        for (var i = 0; i < 10; i++)
        {
            now += LinkProbe.CheckAfter;
            probe.Sweep();
        }

        Assert.Equal(LinkProbe.MaxAttempts, providerStub.Calls.Count(c => c.Name == nameof(MediaBrowser.Controller.Providers.IProviderManager.QueueRefresh)));
        Assert.Equal(0, probe.Waiting);
    }

    private static async Task<bool> RunResult(LinkPlaybackFilter filter, string controllerName, MediaSourceInfo source)
    {
        var response = new PlaybackInfoResponse { MediaSources = new[] { source } };
        var context = new ResultExecutingContext(Action(controllerName), new List<IFilterMetadata>(), new ObjectResult(response), controller: new object());
        var ran = false;
        await filter.OnResultExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult<ResultExecutedContext>(null!);
        });
        return ran;
    }

    private static ActionContext Action(string controllerName, Guid? userId = null, string actionName = "GetPostedPlaybackInfo")
    {
        var route = new RouteData();
        route.Values["itemId"] = Guid.NewGuid().ToString("N");
        var http = new DefaultHttpContext();
        if (userId is { } user)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", user.ToString()) }, "test"));
        }

        return new ActionContext(http, route, new ControllerActionDescriptor { ControllerName = controllerName, ActionName = actionName });
    }

    private static LinkPlaybackFilter Filter(string path, bool isLink, LinkGrants? grants = null, ILinkAccess? access = null)
    {
        var (library, stub) = InterfaceStub.Create<ILibraryManager>();
        stub.Answer(nameof(ILibraryManager.GetItemById), _ => new Movie { Path = path });
        ILinkRegistry links = isLink ? new OneLink(path) : new NoLinks();
        return new LinkPlaybackFilter(library, links, grants ?? new LinkGrants(), access ?? new Access(null), NullLogger<LinkPlaybackFilter>.Instance);
    }

    private static async Task<(ActionExecutingContext Context, bool Ran)> RunAction(LinkPlaybackFilter filter, string controllerName, Guid? userId, string actionName = "GetPostedPlaybackInfo")
    {
        var context = new ActionExecutingContext(Action(controllerName, userId, actionName), new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());
        var ran = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });
        return (context, ran);
    }

    private LinkFiles Files()
    {
        var (host, hostStub) = InterfaceStub.Create<IServerApplicationHost>();
        hostStub.Answer("get_HttpPort", _ => 8096);
        hostStub.Answer(nameof(IServerApplicationHost.GetLocalApiUrl), args => $"{args[1]}://{args[0]}:{args[2]}");
        var (paths, pathsStub) = InterfaceStub.Create<IApplicationPaths>();
        pathsStub.Answer("get_DataPath", _ => _dir);
        return new LinkFiles(host, paths);
    }

    private sealed class Access : ILinkAccess
    {
        private readonly string? _refusal;

        public Access(string? refusal) => _refusal = refusal;

        public int Calls { get; private set; }

        public Task<LinkChoice> ChooseAsync(LinkEntry link, Guid? userId, IReadOnlyCollection<string>? skip, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new LinkChoice(_refusal is null ? LinkFallback.PrimaryOf(link) : null, _refusal));
        }
    }

    private sealed class OneLink : ILinkRegistry
    {
        private readonly string _path;

        public OneLink(string path) => _path = path;

        public string? LinkPathFor(string copiedPath) => null;

        public string? CopiedPathFor(string linkPath) => null;

        public LinkEntry? Find(string linkPath) => linkPath == _path ? new LinkEntry(_path, _path, "abc", "server") : null;
    }

    private sealed class PeerHandler : HttpMessageHandler
    {
        private readonly string _items;
        private readonly string _links;

        public PeerHandler(string items, string links)
        {
            _items = items;
            _links = links;
        }

        public int LinkCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isLinks = request.RequestUri!.AbsolutePath.EndsWith("/Sync/Peer/Links", StringComparison.Ordinal);
            if (isLinks)
            {
                LinkCalls++;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(isLinks ? _links : _items, Encoding.UTF8, "application/json")
            });
        }
    }
}
