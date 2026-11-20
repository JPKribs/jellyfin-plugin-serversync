using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Api;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Api;

/// <summary>
/// The routes behind links hand this server's files and their facts only to a proven server that may link here, and only for what the caller may see.
/// </summary>
public sealed class PeerControllerLinksTests : IDisposable
{
    private const string Secret = "issued-to-the-peer";
    private const string PeerId = ControllerTestHarness.PeerId;
    private const string Gone = "the file is no longer on the source";
    private static readonly Guid ItemId = Guid.Parse("11111111111111111111111111111111");
    private static readonly Guid Scoped = Guid.Parse("22222222222222222222222222222222");

    private readonly ControllerTestHarness _harness = new();
    private readonly LinkSessions _sessions = new();
    private readonly ISessionManager _sessionManager;
    private readonly IMediaSourceManager _mediaSources;
    private readonly IChapterManager _chapters;
    private readonly InterfaceStub _chaptersStub;
    private readonly ITrickplayManager _trickplay;
    private readonly SourceServer _peer;
    private readonly string _file;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerControllerLinksTests"/> class, with a paired Sync peer that may link and one film on disk.
    /// </summary>
    public PeerControllerLinksTests()
    {
        _peer = _harness.AddPeer(ServerMode.Sync);
        _peer.AllowLinking = true;
        _harness.Pairings.SetInbound(_peer.Key, Secret);
        _file = Path.Combine(_harness.TempDir, "Film.mkv");
        File.WriteAllBytes(_file, new byte[1234]);
        _harness.LibraryStub.Answer(nameof(ILibraryManager.GetItemById), args => args[0] is Guid id && id == ItemId ? new Movie { Id = ItemId, Path = _file, RunTimeTicks = 99 } : null);

        (_sessionManager, var sessionStub) = InterfaceStub.Create<ISessionManager>();
        sessionStub.Answer("get_Sessions", _ => Array.Empty<SessionInfo>());
        (_mediaSources, var mediaStub) = InterfaceStub.Create<IMediaSourceManager>();
        mediaStub.Answer(nameof(IMediaSourceManager.GetMediaStreams), _ => new List<MediaStream>());
        mediaStub.Answer(nameof(IMediaSourceManager.GetMediaAttachments), _ => new List<MediaAttachment>());
        (_chapters, _chaptersStub) = InterfaceStub.Create<IChapterManager>();
        _chaptersStub.Answer(nameof(IChapterManager.GetChapters), _ => new List<ChapterInfo>());
        (_trickplay, var trickplayStub) = InterfaceStub.Create<ITrickplayManager>();
        trickplayStub.Answer(nameof(ITrickplayManager.GetTrickplayResolutions), _ => Task.FromResult(new Dictionary<int, Jellyfin.Database.Implementations.Entities.TrickplayInfo>()));
    }

    /// <inheritdoc/>
    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// Every link route that names a sender refuses one that is unknown or has not proven itself, before it reads anything.
    /// True: no file, file fact, or session reaches a caller that only claims to be a listed server.
    /// False: a caller could stream this server's files by naming a listed server's ID.
    /// </summary>
    [Theory]
    [InlineData("ffffffffffffffffffffffffffffffff", Secret, StatusCodes.Status409Conflict)]
    [InlineData(PeerId, "wrong", HintProtocol.UnpairedStatus)]
    [InlineData(PeerId, null, HintProtocol.UnpairedStatus)]
    [InlineData(null, Secret, StatusCodes.Status400BadRequest)]
    public async Task SenderRoutes_UnknownOrUnprovenSender_AreRefused(string? serverId, string? presented, int status)
    {
        var controller = _harness.PeerRoutes(presented);
        var policy = Policy();

        var answers = new[]
        {
            controller.Stream(ItemId, serverId, null, null, probe: true, fill: false, policy, _sessions, _harness.UserManager),
            controller.CheckStream(new LinkStreamCheckRequest { ServerId = serverId!, ItemId = ItemId.ToString("N"), Probe = true }, policy, _harness.UserManager, _harness.Library).Result!,
            await controller.Playback(new PeerPlaybackReport { ServerId = serverId!, ItemId = ItemId.ToString("N"), SessionKey = "s" }, PlaybackSessions()),
            await controller.Control(new PeerControlRequest { ServerId = serverId!, SessionKey = "s" }, _sessionManager, _harness.Library, Registry(), new LinkGrants()),
            (await controller.DescribeLinks(new PeerLinkDescribeRequest { ServerId = serverId!, ItemIds = { ItemId.ToString("N") } }, _harness.Library, _harness.UserManager, _mediaSources, _chapters, _trickplay)).Result!,
            await controller.LinkImage(ItemId, "Chapter", 0, serverId, 0, _harness.Library, _harness.UserManager, _chapters, _trickplay)
        };

        Assert.All(answers, answer => Assert.Equal(status, Assert.IsAssignableFrom<ObjectResult>(answer).StatusCode));
    }

    /// <summary>
    /// A proven server whose entry does not allow linking is refused its stream, its link facts, and its link images with 403.
    /// True: linking is off until the operator switches it on for that entry.
    /// False: any paired server could stream files without the operator's leave.
    /// </summary>
    [Theory]
    [InlineData(false, ServerMode.Sync)]
    [InlineData(true, ServerMode.Pull)]
    public async Task LinkRoutes_LinkingNotAllowedForTheEntry_Answer403(bool allowLinking, ServerMode mode)
    {
        (_peer.AllowLinking, _peer.Mode) = (allowLinking, mode);
        var controller = _harness.PeerRoutes(Secret);

        var stream = controller.Stream(ItemId, PeerId, null, null, probe: true, fill: false, Policy(), _sessions, _harness.UserManager);
        var described = await controller.DescribeLinks(new PeerLinkDescribeRequest { ServerId = PeerId, ItemIds = { ItemId.ToString("N") } }, _harness.Library, _harness.UserManager, _mediaSources, _chapters, _trickplay);
        var image = await controller.LinkImage(ItemId, "Chapter", 0, PeerId, 0, _harness.Library, _harness.UserManager, _chapters, _trickplay);
        var check = controller.CheckStream(new LinkStreamCheckRequest { ServerId = PeerId, ItemId = ItemId.ToString("N"), Probe = true }, Policy(), _harness.UserManager, _harness.Library);

        Assert.All(new[] { stream, described.Result!, image }, answer =>
        {
            var refused = Assert.IsType<ObjectResult>(answer);
            Assert.Equal((StatusCodes.Status403Forbidden, "linking is not allowed for Peer here"), (refused.StatusCode, refused.Value));
        });
        var checkedBody = Assert.IsType<LinkStreamCheckResponse>(Assert.IsType<OkObjectResult>(check.Result).Value);
        Assert.Equal((false, "linking is not allowed for Peer here"), (checkedBody.Allowed, checkedBody.Reason));
    }

    /// <summary>
    /// A probe of a file this server holds is answered with the file, with range support.
    /// True: the linking server can read media info from the start or the end of the file.
    /// False: a link would never get its runtime or streams, or a probe would have to read the whole file.
    /// </summary>
    [Fact]
    public void Stream_ProbeOfAFileHere_AnswersTheFileWithRanges()
    {
        var result = _harness.PeerRoutes(Secret).Stream(ItemId, PeerId, null, null, probe: true, fill: false, Policy(), _sessions, _harness.UserManager);

        var file = Assert.IsType<FileStreamResult>(result);
        using (file.FileStream)
        {
            Assert.True(file.EnableRangeProcessing);
            Assert.Equal(1234, file.FileStream.Length);
            Assert.Equal("video/x-matroska", file.ContentType);
            Assert.NotNull(file.LastModified);
        }
    }

    /// <summary>
    /// A stream is refused with 403 and the reason when the file is gone, is itself a link, or no user is mapped.
    /// True: the linking server hears why, and can fall back or tell its viewer.
    /// False: a missing file would surface as a server error, or a stream would start for nobody.
    /// </summary>
    [Theory]
    [InlineData("missing-item", true, Gone)]
    [InlineData("file-deleted", true, Gone)]
    [InlineData("is-a-link", true, "the item is a link on the source, not a file")]
    [InlineData("here", false, "your account is not mapped to a user on the source")]
    public void Stream_FileGoneLinkOrNoUser_Answers403WithTheReason(string scenario, bool probe, string reason)
    {
        var itemId = scenario == "missing-item" ? Guid.NewGuid() : ItemId;
        Arrange(scenario);

        var result = _harness.PeerRoutes(Secret).Stream(itemId, PeerId, probe ? null : "not-a-user", null, probe, fill: false, Policy(), _sessions, _harness.UserManager);

        var refused = Assert.IsType<ObjectResult>(result);
        Assert.Equal((StatusCodes.Status403Forbidden, reason), (refused.StatusCode, refused.Value));
    }

    /// <summary>
    /// A standard user's key whose user is gone cannot probe a file.
    /// True: a scoped key reads only what its user can see, and nothing once the user is removed.
    /// False: a standard user's key could probe every file on this server.
    /// </summary>
    [Fact]
    public void Stream_ScopedCallerWhoseUserIsGone_IsRefused()
    {
        var result = _harness.PeerRoutes(Secret, Scoped).Stream(ItemId, PeerId, null, null, probe: true, fill: false, Policy(), _sessions, _harness.UserManager);

        var refused = Assert.IsType<ObjectResult>(result);
        Assert.Equal((StatusCodes.Status403Forbidden, Gone), (refused.StatusCode, refused.Value));
    }

    /// <summary>
    /// Cache downloads to one server run at most two at once, and a third is answered 429.
    /// True: uncounted cache downloads cannot take all of this server's upload.
    /// False: a linking server could open any number of full file downloads at once.
    /// </summary>
    [Fact]
    public void Stream_CacheDownloads_AreHeldToTwoAtOnce()
    {
        IActionResult Fill() => _harness.PeerRoutes(Secret).Stream(ItemId, PeerId, null, null, probe: true, fill: true, Policy(), _sessions, _harness.UserManager);

        var first = Fill();
        var second = Fill();
        var third = Fill();

        Assert.Equal(_file, Assert.IsType<PhysicalFileResult>(first).FileName);
        Assert.True(Assert.IsType<PhysicalFileResult>(second).EnableRangeProcessing);
        Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsType<ObjectResult>(third).StatusCode);
    }

    /// <summary>
    /// A stream check without a body, an ID or a path, or with a malformed ID or an overlong path, is refused with 400.
    /// True: a malformed check reaches neither the sender check nor the library.
    /// False: a malformed check would be answered as if it named a file.
    /// </summary>
    [Theory]
    [InlineData("no-body")]
    [InlineData("neither")]
    [InlineData("bad-id")]
    [InlineData("blank-path")]
    [InlineData("path-too-long")]
    public void CheckStream_MalformedRequest_Answers400(string scenario)
    {
        var request = scenario switch
        {
            "no-body" => null,
            "neither" => new LinkStreamCheckRequest { ServerId = PeerId },
            "bad-id" => new LinkStreamCheckRequest { ServerId = PeerId, ItemId = "not-an-id", Path = "/m/Film.mkv" },
            "blank-path" => new LinkStreamCheckRequest { ServerId = PeerId, ItemId = string.Empty, Path = "  " },
            _ => new LinkStreamCheckRequest { ServerId = PeerId, ItemId = ItemId.ToString("N"), Path = new string('a', 4097) }
        };

        var result = _harness.PeerRoutes(Secret).CheckStream(request!, Policy(), _harness.UserManager, _harness.Library);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    /// <summary>
    /// An allowed check answers with the item's ID, the file's size, the streams in use, and the stream limit, never below zero.
    /// True: the linking server knows the file is there, how large it is, and how much room the source has.
    /// False: a link would be written without a size, or a negative limit would read as a real one.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(-5, 0)]
    public void CheckStream_Allowed_AnswersTheItemItsSizeAndTheLimit(int maxStreams, int expectedLimit)
    {
        _peer.LinkMaxStreams = maxStreams;
        using var open = _sessions.Open(_peer.Key, Guid.NewGuid(), Guid.NewGuid());

        var result = _harness.PeerRoutes(Secret).CheckStream(new LinkStreamCheckRequest { ServerId = PeerId, ItemId = ItemId.ToString("D"), Probe = true }, Policy(), _harness.UserManager, _harness.Library, _sessions);

        var body = Assert.IsType<LinkStreamCheckResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((true, ItemId.ToString("N"), 1234L, 1, expectedLimit), (body.Allowed, body.ItemId, body.Size, body.StreamsInUse, body.StreamLimit));
    }

    /// <summary>
    /// A check by path finds the file this server holds at that path, and says so when it holds none.
    /// True: a linking server can find the same film here to fall back to.
    /// False: fallback would never find a file, or would be told one exists when it does not.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CheckStream_ByPath_FindsTheFileOrSaysItIsNotHere(bool held)
    {
        _harness.LibraryStub.Answer(nameof(ILibraryManager.FindByPath), _ => held ? new Movie { Id = ItemId, Path = _file } : null);

        var result = _harness.PeerRoutes(Secret).CheckStream(new LinkStreamCheckRequest { ServerId = PeerId, Path = "/media/Film.mkv", Probe = true }, Policy(), _harness.UserManager, _harness.Library);

        var body = Assert.IsType<LinkStreamCheckResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((held, held ? null : "the file is not on this server", held ? ItemId.ToString("N") : null), (body.Allowed, body.Reason, body.ItemId));
        Assert.Equal(0, body.StreamsInUse);
    }

    /// <summary>
    /// A playback report without a body, or with an overlong session key, client name, or user name, is refused with 400.
    /// True: a malformed report creates no session here.
    /// False: an oversized name would be shown on this server's dashboard.
    /// </summary>
    [Theory]
    [InlineData("no-body")]
    [InlineData("session-key")]
    [InlineData("client-name")]
    [InlineData("user-name")]
    public async Task Playback_MalformedReport_Answers400(string scenario)
    {
        var tooLong = new string('a', 257);
        var report = scenario switch
        {
            "no-body" => null,
            "session-key" => new PeerPlaybackReport { ServerId = PeerId, SessionKey = tooLong },
            "client-name" => new PeerPlaybackReport { ServerId = PeerId, SessionKey = "s", ClientName = tooLong },
            _ => new PeerPlaybackReport { ServerId = PeerId, SessionKey = "s", UserName = tooLong }
        };

        var result = await _harness.PeerRoutes(Secret).Playback(report!, PlaybackSessions());

        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>
    /// A playback report is refused with 403 and the reason when it names no item here, or comes through another user's key.
    /// True: a report can only show a session for an item this server has, as the user the key belongs to.
    /// False: a standard user's key could report playback, and move played state, for another user.
    /// </summary>
    [Theory]
    [InlineData(false, "not-an-id", "no such item here")]
    [InlineData(false, "99999999999999999999999999999999", "no such item here")]
    [InlineData(true, "11111111111111111111111111111111", "the source's key for this server belongs to another user")]
    public async Task Playback_UnknownItemOrAnotherUsersKey_Answers403WithTheReason(bool scoped, string itemId, string reason)
    {
        var report = new PeerPlaybackReport { ServerId = PeerId, ItemId = itemId, SessionKey = "s", UserId = Guid.NewGuid().ToString("N") };

        var result = await _harness.PeerRoutes(Secret, scoped ? Scoped : null).Playback(report, PlaybackSessions());

        var refused = Assert.IsType<ObjectResult>(result);
        Assert.Equal((StatusCodes.Status403Forbidden, reason), (refused.StatusCode, refused.Value));
    }

    /// <summary>
    /// A control request without a body or a session key, or with overlong text or header, is refused with 400.
    /// True: a malformed command reaches no viewer.
    /// False: an oversized message could be pushed to a viewer's screen.
    /// </summary>
    [Theory]
    [InlineData("no-body")]
    [InlineData("no-session")]
    [InlineData("text-too-long")]
    [InlineData("header-too-long")]
    public async Task Control_MalformedRequest_Answers400(string scenario)
    {
        var request = scenario switch
        {
            "no-body" => null,
            "no-session" => new PeerControlRequest { ServerId = PeerId, SessionKey = " " },
            "text-too-long" => new PeerControlRequest { ServerId = PeerId, SessionKey = "s", Text = new string('a', 1025) },
            _ => new PeerControlRequest { ServerId = PeerId, SessionKey = "s", Header = new string('a', 257) }
        };

        var result = await _harness.PeerRoutes(Secret).Control(request!, _sessionManager, _harness.Library, Registry(), new LinkGrants());

        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>
    /// A command for a session that is not here is answered 404.
    /// True: a server can only control a viewer that is playing one of its own links.
    /// False: a paired server could send commands to any session on this server.
    /// </summary>
    [Fact]
    public async Task Control_NoSuchSession_Answers404()
    {
        var request = new PeerControlRequest { ServerId = PeerId, SessionKey = "no-such-session", Command = "Pause" };

        var result = await _harness.PeerRoutes(Secret).Control(request, _sessionManager, _harness.Library, Registry(), new LinkGrants());

        Assert.IsType<NotFoundObjectResult>(result);
    }

    /// <summary>
    /// A describe request without a body or a list, or naming more than 200 items, is refused with 400, and 200 is taken.
    /// True: the batch limit holds at its edge.
    /// False: one request could make this server stat any number of files.
    /// </summary>
    [Theory]
    [InlineData(-1, false)]
    [InlineData(PeerLinkDescribeRequest.MaxItems, true)]
    [InlineData(PeerLinkDescribeRequest.MaxItems + 1, false)]
    public async Task DescribeLinks_BatchSize_IsHeldTo200(int count, bool taken)
    {
        var request = count < 0 ? new PeerLinkDescribeRequest { ServerId = PeerId, ItemIds = null! } : new PeerLinkDescribeRequest { ServerId = PeerId, ItemIds = Enumerable.Range(0, count).Select(_ => "x").ToList() };

        var result = await Describe(request);
        var none = await Describe(null!);

        Assert.Equal(taken, result.Result is OkObjectResult);
        Assert.Equal(!taken, result.Result is BadRequestObjectResult);
        Assert.IsType<BadRequestObjectResult>(none.Result);
    }

    /// <summary>
    /// Each item is answered in order, with its size when the file is here and the reason when it is not.
    /// True: the linking server keeps links whose file is here and removes the rest, each for its own reason.
    /// False: a link to a removed file would be kept, or a live one removed.
    /// </summary>
    [Fact]
    public async Task DescribeLinks_MixedItems_AnswersEachInOrder()
    {
        var folder = Guid.NewGuid();
        var link = Guid.NewGuid();
        var deleted = Guid.NewGuid();
        _harness.LibraryStub.Answer(nameof(ILibraryManager.GetItemById), args => (Guid)args[0]! switch
        {
            var id when id == ItemId => new Movie { Id = ItemId, Path = _file },
            var id when id == folder => new Folder { Id = folder, Path = _harness.TempDir },
            var id when id == link => new Movie { Id = link, Path = "/m/Other.strm" },
            var id when id == deleted => new Movie { Id = deleted, Path = Path.Combine(_harness.TempDir, "Deleted.mkv") },
            _ => null
        });
        var ids = new List<string> { ItemId.ToString("N"), "not-an-id", Guid.NewGuid().ToString("N"), folder.ToString("N"), link.ToString("N"), deleted.ToString("N") };

        var result = await Describe(new PeerLinkDescribeRequest { ServerId = PeerId, ItemIds = ids });

        var items = Assert.IsType<PeerLinkDescribeResponse>(Assert.IsType<OkObjectResult>(result.Result).Value).Items;
        Assert.Equal(ids, items.Select(i => i.ItemId));
        Assert.Equal((true, 1234L, null, false), (items[0].Exists, items[0].Size, items[0].Reason, items[0].HasDetails));
        Assert.Equal(new string?[] { Gone, Gone, Gone, "the item is a link on the source, not a file", Gone }, items.Skip(1).Select(i => i.Reason).ToArray());
        Assert.All(items.Skip(1), i => Assert.False(i.Exists));
    }

    /// <summary>
    /// Details are sent only when asked for, and carry the item's media info.
    /// True: a new link gets its runtime and streams, and a routine check stays light.
    /// False: every check would read media info, or a new link would show no runtime.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DescribeLinks_Details_AreSentOnlyWhenAsked(bool details)
    {
        var result = await Describe(new PeerLinkDescribeRequest { ServerId = PeerId, ItemIds = { ItemId.ToString("N") }, Details = details });

        var item = Assert.Single(Assert.IsType<PeerLinkDescribeResponse>(Assert.IsType<OkObjectResult>(result.Result).Value).Items);
        Assert.Equal((true, details, details ? 99L : (long?)null), (item.Exists, item.HasDetails, item.RunTimeTicks));
        Assert.Empty(item.Streams);
        Assert.Empty(item.Chapters);
        Assert.Empty(item.Trickplay);
    }

    /// <summary>
    /// A standard user's key whose user is gone is told every file is gone, and reads no image.
    /// True: a scoped key learns nothing about files its user cannot see.
    /// False: a standard user's key could list sizes and images for the whole library.
    /// </summary>
    [Fact]
    public async Task DescribeLinksAndLinkImage_ScopedCallerWhoseUserIsGone_ReadNothing()
    {
        var controller = _harness.PeerRoutes(Secret, Scoped);
        ChapterImage();

        var described = await controller.DescribeLinks(new PeerLinkDescribeRequest { ServerId = PeerId, ItemIds = { ItemId.ToString("N") } }, _harness.Library, _harness.UserManager, _mediaSources, _chapters, _trickplay);
        var image = await controller.LinkImage(ItemId, "Chapter", 0, PeerId, 0, _harness.Library, _harness.UserManager, _chapters, _trickplay);

        var item = Assert.Single(Assert.IsType<PeerLinkDescribeResponse>(Assert.IsType<OkObjectResult>(described.Result).Value).Items);
        Assert.Equal((false, Gone), (item.Exists, item.Reason));
        Assert.IsType<NotFoundResult>(image);
    }

    /// <summary>
    /// A chapter image this server made is sent as a JPEG.
    /// True: a link shows chapter images Jellyfin never makes for it.
    /// False: links would have no chapter images.
    /// </summary>
    [Fact]
    public async Task LinkImage_ChapterImageHere_IsSent()
    {
        var path = ChapterImage();

        var result = await _harness.PeerRoutes(Secret).LinkImage(ItemId, "chapter", 0, PeerId, 0, _harness.Library, _harness.UserManager, _chapters, _trickplay);

        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal((path, "image/jpeg"), (file.FileName, file.ContentType));
    }

    /// <summary>
    /// An image is answered 404 for an unknown item, a negative or unused index, an unknown kind, a link, or a file that is gone.
    /// True: only an image that exists for a real file here is ever sent.
    /// False: a bad index would throw, or a link's borrowed image would be handed on.
    /// </summary>
    [Theory]
    [InlineData("unknown-item", "Chapter", 0)]
    [InlineData("here", "Chapter", -1)]
    [InlineData("here", "Chapter", 5)]
    [InlineData("here", "Poster", 0)]
    [InlineData("here", "", 0)]
    [InlineData("is-a-link", "Chapter", 0)]
    [InlineData("image-deleted", "Chapter", 0)]
    public async Task LinkImage_NothingToSend_Answers404(string scenario, string kind, int index)
    {
        var image = ChapterImage();
        Arrange(scenario);
        if (scenario == "image-deleted")
        {
            File.Delete(image);
        }

        var result = await _harness.PeerRoutes(Secret).LinkImage(scenario == "unknown-item" ? Guid.NewGuid() : ItemId, kind, index, PeerId, 0, _harness.Library, _harness.UserManager, _chapters, _trickplay);

        Assert.IsType<NotFoundResult>(result);
    }

    /// <summary>
    /// This server's links are listed with the path each copy would have, and its copies are not.
    /// True: a peer matches a link here by the file it stands in for.
    /// False: a peer would read a link here as a real file and link to a link.
    /// </summary>
    [Fact]
    public void Links_LinksAndCopies_ListsOnlyLinksWithTheirCopiedPath()
    {
        _harness.AddContent("link", change: r => (r.StorageMode, r.LocalPath, r.SourcePath) = (StorageMode.Link, "/m/Film/Film.strm", "/src/Film/Film.mkv"));
        _harness.AddContent("promoting", change: r => (r.StorageMode, r.LocalPath, r.SourcePath) = (StorageMode.Promoting, "/m/Show/Ep.strm", "/src/Show/Ep.mp4"));
        _harness.AddContent("copy", change: r => r.LocalPath = "/m/Copy/Copy.mkv");

        var result = _harness.PeerRoutes().Links(_harness.Content, _harness.UserManager, _harness.Library);

        var links = Assert.IsType<PeerLinksResponse>(Assert.IsType<OkObjectResult>(result.Result).Value).Links;
        Assert.Equal(
            new[] { ("/m/Film/Film.strm", "/m/Film/Film.mkv"), ("/m/Show/Ep.strm", "/m/Show/Ep.mp4") },
            links.Select(l => (l.LinkPath, l.CopiedPath)).OrderBy(l => l.LinkPath, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// This server's copies from other servers are listed with their origin, with copies removed in the last day, and its own files are not.
    /// True: a peer can tell a copy here from this server's own file, even just after the copy went.
    /// False: a peer would sync a copy back to the server it came from.
    /// </summary>
    [Fact]
    public void Copies_CopiesWithAnOrigin_AreListedWithRecentlyRemovedOnes()
    {
        _harness.AddContent("copy", change: r => (r.LocalPath, r.OriginServerId) = ("/m/Copy.mkv", "origin-1"));
        _harness.AddContent("own", change: r => r.LocalPath = "/m/Own.mkv");
        _harness.AddContent("link", change: r => (r.StorageMode, r.LocalPath, r.OriginServerId) = (StorageMode.Link, "/m/Link.strm", "origin-1"));
        _harness.Content.NoteRemovedCopy("/m/Removed.mkv", "origin-2");
        _harness.Content.NoteRemovedCopy("/m/COPY.mkv", "origin-9");

        var result = _harness.PeerRoutes().Copies(_harness.Content, _harness.UserManager, _harness.Library);

        var copies = Assert.IsType<PeerCopiesResponse>(Assert.IsType<OkObjectResult>(result.Result).Value).Copies;
        Assert.Equal(
            new[] { ("/m/Copy.mkv", "origin-1"), ("/m/Removed.mkv", "origin-2") },
            copies.Select(c => (c.Path, c.OriginServerId)).OrderBy(c => c.Path, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// A standard user's key whose user is gone reads no links and no copies.
    /// True: a scoped key learns no paths its user cannot see.
    /// False: a standard user's key could list every linked and copied path on this server.
    /// </summary>
    [Fact]
    public void LinksAndCopies_ScopedCallerWhoseUserIsGone_ReadNothing()
    {
        _harness.AddContent("link", change: r => (r.StorageMode, r.LocalPath) = (StorageMode.Link, "/m/Film.strm"));
        _harness.AddContent("copy", change: r => (r.LocalPath, r.OriginServerId) = ("/m/Copy.mkv", "origin-1"));
        _harness.Content.NoteRemovedCopy("/m/Removed.mkv", "origin-2");
        var controller = _harness.PeerRoutes(scopedUser: Scoped);

        var links = controller.Links(_harness.Content, _harness.UserManager, _harness.Library);
        var copies = controller.Copies(_harness.Content, _harness.UserManager, _harness.Library);

        Assert.Empty(Assert.IsType<PeerLinksResponse>(Assert.IsType<OkObjectResult>(links.Result).Value).Links);
        Assert.Empty(Assert.IsType<PeerCopiesResponse>(Assert.IsType<OkObjectResult>(copies.Result).Value).Copies);
    }

    /// <summary>
    /// The removed items since a time are listed, and a time before the record began is marked too old.
    /// True: a peer learns which of its copies' sources left, and knows when it must do a full check.
    /// False: a peer would keep copies of removed files, or trust a record that does not reach back far enough.
    /// </summary>
    [Fact]
    public void Changes_RemovedSinceATime_AreListedAndAnOldTimeIsMarked()
    {
        var now = DateTime.UtcNow;
        _harness.Removed.Note("old", now.AddDays(-3));
        _harness.Removed.Note("recent", now.AddMinutes(-5));
        var controller = _harness.PeerRoutes();

        var recent = Assert.IsType<PeerChangesResponse>(Assert.IsType<OkObjectResult>(controller.Changes(now.AddHours(-1), _harness.Removed).Result).Value);
        var all = Assert.IsType<PeerChangesResponse>(Assert.IsType<OkObjectResult>(controller.Changes(DateTime.SpecifyKind(now.AddDays(-10), DateTimeKind.Unspecified), _harness.Removed).Result).Value);
        var future = Assert.IsType<PeerChangesResponse>(Assert.IsType<OkObjectResult>(controller.Changes(now.AddDays(1), _harness.Removed).Result).Value);

        Assert.Equal(new[] { "recent" }, recent.RemovedItemIds);
        Assert.Equal(new[] { "old", "recent" }, all.RemovedItemIds.OrderBy(i => i, StringComparer.Ordinal).ToArray());
        Assert.True(all.TooOld);
        Assert.Equal((false, 0), (future.TooOld, future.RemovedItemIds.Count));
    }

    /// <summary>
    /// A changes request without a time is refused with 400.
    /// True: the caller must say how far back it needs.
    /// False: a missing time would list every removal ever recorded.
    /// </summary>
    [Fact]
    public void Changes_NoSinceTime_Answers400()
    {
        var result = _harness.PeerRoutes().Changes(null, _harness.Removed);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    /// <summary>
    /// Each link route answers its own verbs and path, and checks the caller's protocol.
    /// True: every link route is reached only by a peer that speaks this server's protocol, through the controller's Whitelist guard.
    /// False: a mismatched peer could stream files or read link facts.
    /// </summary>
    [Theory]
    [InlineData(nameof(PeerController.Stream), "GET,HEAD", "Stream/{itemId}")]
    [InlineData(nameof(PeerController.CheckStream), "POST", "Stream/Check")]
    [InlineData(nameof(PeerController.Playback), "POST", "Playback")]
    [InlineData(nameof(PeerController.Control), "POST", "Control")]
    [InlineData(nameof(PeerController.DescribeLinks), "POST", "Links/Describe")]
    [InlineData(nameof(PeerController.LinkImage), "GET", "Links/{itemId}/Images/{kind}/{index}")]
    [InlineData(nameof(PeerController.Links), "GET", "Links")]
    [InlineData(nameof(PeerController.Changes), "GET", "Changes")]
    [InlineData(nameof(PeerController.Copies), "GET", "Copies")]
    public void Route_CarriesItsVerbsPathAndProtocolCheck(string action, string verbs, string template)
    {
        var method = ControllerRouteAssert.Route(typeof(PeerController), action, verbs, template);

        Assert.NotNull(method.GetCustomAttribute<RequireCompatibleProtocolAttribute>());
        Assert.NotNull(typeof(PeerController).GetCustomAttribute<PeerCallerGuardAttribute>());
    }

    private static ILinkRegistry Registry() => InterfaceStub.Create<ILinkRegistry>().Proxy;

    private LinkPolicy Policy() => new(_harness.Library, _harness.UserManager, _sessionManager, _sessions);

    private LinkPlaybackSessions PlaybackSessions()
        => new(_sessionManager, _harness.Library, _harness.UserManager, _harness.Config, _harness.Clients, _harness.Host, NullLogger<LinkSessionController>.Instance);

    private Task<ActionResult<PeerLinkDescribeResponse>> Describe(PeerLinkDescribeRequest request)
        => _harness.PeerRoutes(Secret).DescribeLinks(request, _harness.Library, _harness.UserManager, _mediaSources, _chapters, _trickplay);

    private string ChapterImage()
    {
        var path = Path.Combine(_harness.TempDir, "chapter-0.jpg");
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xD8, 0xFF });
        _chaptersStub.Answer(nameof(IChapterManager.GetChapters), _ => new List<ChapterInfo> { new() { ImagePath = path } });
        return path;
    }

    private void Arrange(string scenario)
    {
        if (scenario == "file-deleted")
        {
            File.Delete(_file);
        }

        if (scenario == "is-a-link")
        {
            var link = Path.Combine(_harness.TempDir, "Film.strm");
            File.WriteAllText(link, "http://127.0.0.1:8096/Sync/Link/abc?k=secret");
            _harness.LibraryStub.Answer(nameof(ILibraryManager.GetItemById), _ => new Movie { Id = ItemId, Path = link });
        }
    }
}
