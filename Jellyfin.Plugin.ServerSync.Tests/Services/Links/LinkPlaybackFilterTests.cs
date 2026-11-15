using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// The link playback filter must let a link be read only by a user the source allowed, and keep every link address out of Jellyfin's responses.
/// </summary>
public sealed class LinkPlaybackFilterTests
{
    private const string LinkPath = "/media/Film.strm";
    private const string LinkUrl = "http://127.0.0.1:8096/Sync/Link/aaaa?k=secret";
    private static readonly Guid User = Guid.Parse("11111111111111111111111111111111");
    private readonly Movie _movie = new() { Id = Guid.NewGuid(), Path = LinkPath };
    private readonly LinkFakeRegistry _registry = new LinkFakeRegistry().Add(new LinkEntry(LinkPath, "/media/Film.mkv", "aaaa", "a"));
    private readonly LinkGrants _grants = new();
    private readonly InterfaceStub _access;
    private readonly ILinkAccess _chooser;
    private readonly ILibraryManager _library;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkPlaybackFilterTests"/> class.
    /// </summary>
    public LinkPlaybackFilterTests()
    {
        (_library, var libraryStub) = InterfaceStub.Create<ILibraryManager>();
        libraryStub.Answer(nameof(ILibraryManager.GetItemById), _ => _movie);
        (_chooser, _access) = InterfaceStub.Create<ILinkAccess>();
        _access.Answer(nameof(ILinkAccess.ChooseAsync), args => Task.FromResult(new LinkChoice(LinkFallback.PrimaryOf((LinkEntry)args[0]!), null)));
    }

    /// <summary>
    /// The filter and its two hooks refuse missing arguments.
    /// True: the mistake is named where it was made.
    /// False: a null would throw inside every Jellyfin request the filter sees.
    /// </summary>
    [Fact]
    public async Task Members_NullArguments_Throw()
    {
        var logger = NullLogger<LinkPlaybackFilter>.Instance;
        var filter = Filter();
        var executing = Executing("MediaInfo", "GetPostedPlaybackInfo", User);
        var result = new ResultExecutingContext(executing, new List<IFilterMetadata>(), new OkResult(), new object());

        Assert.Throws<ArgumentNullException>(() => new LinkPlaybackFilter(null!, _registry, _grants, _chooser, logger));
        Assert.Throws<ArgumentNullException>(() => new LinkPlaybackFilter(_library, null!, _grants, _chooser, logger));
        Assert.Throws<ArgumentNullException>(() => new LinkPlaybackFilter(_library, _registry, null!, _chooser, logger));
        Assert.Throws<ArgumentNullException>(() => new LinkPlaybackFilter(_library, _registry, _grants, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new LinkPlaybackFilter(_library, _registry, _grants, _chooser, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => filter.OnActionExecutionAsync(null!, () => Task.FromResult<ActionExecutedContext>(null!)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => filter.OnActionExecutionAsync(executing, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => filter.OnResultExecutionAsync(null!, () => Task.FromResult<ResultExecutedContext>(null!)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => filter.OnResultExecutionAsync(result, null!));
    }

    /// <summary>
    /// A play request for a link with no user, or the empty user id, is refused and no source is asked.
    /// True: every link play is tied to a known user before a source is asked.
    /// False: an anonymous request would be approved as nobody and grant the link to the next reader.
    /// </summary>
    /// <param name="claim">The user id claim on the request, or null for none.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("not-a-guid")]
    public async Task OnActionExecutionAsync_PlayWithoutAUser_IsRefused(string? claim)
    {
        var context = Executing("MediaInfo", "GetPostedPlaybackInfo", null, claim);

        var ran = await Run(Filter(), context);

        var refused = Assert.IsType<ObjectResult>(context.Result);
        Assert.False(ran);
        Assert.Equal(StatusCodes.Status403Forbidden, refused.StatusCode);
        Assert.Equal("This item streams from another server, which refused: this server could not tell who is playing", refused.Value);
        Assert.Empty(_access.Calls);
        Assert.Null(_grants.Use("aaaa"));
    }

    /// <summary>
    /// A request to a controller or action the filter does not watch goes through without a link lookup.
    /// True: the filter costs ordinary requests nothing.
    /// False: every Jellyfin request would look its item up in the link table.
    /// </summary>
    /// <param name="controller">The request's controller.</param>
    /// <param name="action">The request's action.</param>
    [Theory]
    [InlineData("Items", "GetItems")]
    [InlineData("MediaInfo", "GetBitrateTestBytes")]
    [InlineData("Videos", "GetAdditionalPart")]
    [InlineData("Subtitle", "SearchRemoteSubtitles")]
    [InlineData("Library", "GetSimilarItems")]
    public async Task OnActionExecutionAsync_UnwatchedRoute_PassesWithoutALookup(string controller, string action)
    {
        var context = Executing(controller, action, User);

        var ran = await Run(Filter(), context);

        Assert.True(ran);
        Assert.Null(context.Result);
        Assert.Equal(0, _registry.Finds);
    }

    /// <summary>
    /// A granted direct play is streamed by this server only when a streamer is present, the setting allows it, and the request is a static video read.
    /// True: Stream Links Directly decides whether the plugin or Jellyfin serves a direct play.
    /// False: the setting would be ignored, or a transcode request would be answered with the raw file.
    /// </summary>
    /// <param name="hasStreamer">Whether a streamer is present.</param>
    /// <param name="direct">Whether Stream Links Directly is on.</param>
    /// <param name="query">The request's query string.</param>
    /// <param name="streamed">Whether the plugin streams the play.</param>
    [Theory]
    [InlineData(true, true, "?static=true", true)]
    [InlineData(true, true, "?static=TRUE", true)]
    [InlineData(true, true, "?static=false", false)]
    [InlineData(true, true, "", false)]
    [InlineData(true, false, "?static=true", false)]
    [InlineData(false, true, "?static=true", false)]
    public async Task OnActionExecutionAsync_GrantedStream_IsStreamedHereOnlyAsDirectPlay(bool hasStreamer, bool direct, string query, bool streamed)
    {
        _grants.Grant("aaaa", User, new LinkRoute("b", "bbbb"));
        var config = new FakeConfigManager(new PluginConfiguration { StreamLinksDirectly = direct });
        var context = Executing("Videos", "GetVideoStream", User);
        context.HttpContext.Request.QueryString = new QueryString(query);

        var ran = await Run(Filter(hasStreamer ? Streamer() : null, config), context);

        Assert.Equal(!streamed, ran);
        if (streamed)
        {
            var result = Assert.IsType<LinkStreamResult>(context.Result);
            Assert.Equal(new LinkGrant(User, false, new LinkRoute("b", "bbbb")), result.Grant);
        }
        else
        {
            Assert.Null(context.Result);
        }
    }

    /// <summary>
    /// A page of items loses the address of each link source and keeps every other source as it was.
    /// True: no client ever receives a link's loopback URL and its secret.
    /// False: a library listing would hand every link's secret to any signed in user.
    /// </summary>
    [Fact]
    public async Task OnResultExecutionAsync_PageOfItems_ClearsOnlyLinkAddresses()
    {
        var link = new MediaSourceInfo { Id = "not-a-guid", Path = LinkUrl, Protocol = MediaProtocol.Http, IsRemote = true };
        var users = new MediaSourceInfo { Path = "http://example.invalid/stream", Protocol = MediaProtocol.Http, IsRemote = true };
        var file = new MediaSourceInfo { Path = "/media/Other.mkv", Protocol = MediaProtocol.File };
        var page = new QueryResult<BaseItemDto>(new[]
        {
            new BaseItemDto { MediaSources = new[] { link, users } },
            new BaseItemDto { MediaSources = new[] { file } },
            new BaseItemDto()
        });

        var ran = await RunResult(Filter(), new ObjectResult(page));

        Assert.True(ran);
        Assert.Equal((null, MediaProtocol.File, false), (link.Path, link.Protocol, link.IsRemote));
        Assert.Equal(("http://example.invalid/stream", MediaProtocol.Http, true), (users.Path, users.Protocol, users.IsRemote));
        Assert.Equal("/media/Other.mkv", file.Path);
    }

    /// <summary>
    /// A lazily built list of items is made concrete, so the cleared addresses are the ones written out.
    /// True: the response that leaves the server is the one the filter cleaned.
    /// False: the list would be built again when written, with every link address back in it.
    /// </summary>
    [Fact]
    public async Task OnResultExecutionAsync_LazyList_IsMadeConcreteAndCleared()
    {
        var result = new ObjectResult(Enumerable.Range(0, 2).Select(_ => new BaseItemDto { MediaSources = new[] { new MediaSourceInfo { Path = LinkUrl } } }));

        await RunResult(Filter(), result);

        var items = Assert.IsType<List<BaseItemDto>>(result.Value);
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Null(item.MediaSources[0].Path));
    }

    /// <summary>
    /// A result with no value, or one that is not about items, goes out unchanged.
    /// True: the filter leaves every other Jellyfin response alone.
    /// False: a plain text or empty response would be broken by the filter.
    /// </summary>
    /// <param name="hasValue">Whether the result carries a value.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnResultExecutionAsync_OtherResults_AreUntouched(bool hasValue)
    {
        var result = new ObjectResult(hasValue ? "plain text" : null);

        var ran = await RunResult(Filter(), result);

        Assert.True(ran);
        Assert.Equal(hasValue ? "plain text" : null, result.Value);
    }

    private static async Task<bool> Run(LinkPlaybackFilter filter, ActionExecutingContext context)
    {
        var ran = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });
        return ran;
    }

    private async Task<bool> RunResult(LinkPlaybackFilter filter, ObjectResult result)
    {
        var context = new ResultExecutingContext(Executing("Items", "GetItems", User), new List<IFilterMetadata>(), result, new object());
        var ran = false;
        await filter.OnResultExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult<ResultExecutedContext>(null!);
        });
        return ran;
    }

    private ActionExecutingContext Executing(string controller, string action, Guid? user, string? rawClaim = null)
    {
        var route = new RouteData();
        route.Values["itemId"] = _movie.Id.ToString("N");
        var http = new DefaultHttpContext();
        var claim = rawClaim ?? user?.ToString();
        if (claim is not null)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", claim) }, "test"));
        }

        var context = new ActionContext(http, route, new ControllerActionDescriptor { ControllerName = controller, ActionName = action });
        return new ActionExecutingContext(context, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
    }

    private LinkPlaybackFilter Filter(LinkStreamer? streamer = null, IPluginConfigurationManager? config = null)
        => new(_library, _registry, _grants, _chooser, NullLogger<LinkPlaybackFilter>.Instance, streamer, config);

    private LinkStreamer Streamer()
    {
        var (host, _) = InterfaceStub.Create<IServerApplicationHost>();
        return new LinkStreamer(new FakeConfigManager(), new FakeClientFactory(), host, _chooser, _grants, new LinkHealth(), NullLogger<LinkStreamer>.Instance, Timeout.InfiniteTimeSpan);
    }
}
