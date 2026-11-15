using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Controllers;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Controllers;

/// <summary>
/// The link route streams a file only to this server itself, only with the link's own secret, and only while someone here asked to play it.
/// </summary>
public sealed class LinkControllerTests : IDisposable
{
    private const string LinkId = "abc123";

    private readonly ControllerTestHarness _harness = new();
    private readonly LinkGrants _grants = new();

    /// <inheritdoc/>
    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// A request counts as this server's own only when both ends are loopback addresses.
    /// True: IPv4, IPv6, and IPv4 mapped loopback pass, and any other or missing address does not.
    /// False: another machine could use a link's URL, or Jellyfin's own ffmpeg would be refused.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1", true)]
    [InlineData("::1", "::1", true)]
    [InlineData("::ffff:127.0.0.1", "::ffff:127.0.0.1", true)]
    [InlineData("127.0.0.1", "::1", true)]
    [InlineData("127.255.255.254", "127.0.0.1", true)]
    [InlineData("192.168.1.20", "127.0.0.1", false)]
    [InlineData("127.0.0.1", "192.168.1.20", false)]
    [InlineData("::ffff:192.168.1.20", "127.0.0.1", false)]
    [InlineData(null, "127.0.0.1", false)]
    [InlineData("127.0.0.1", null, false)]
    [InlineData(null, null, false)]
    public void FromThisServer_ByAddresses_NeedsLoopbackAtBothEnds(string? remote, string? local, bool expected)
    {
        var context = Request(remote, local);

        var fromHere = LinkController.FromThisServer(context);

        Assert.Equal(expected, fromHere);
    }

    /// <summary>
    /// Any header a proxy adds marks a loopback request as passed on from elsewhere.
    /// True: a request a reverse proxy on this machine forwarded is refused.
    /// False: anyone reaching the proxy could stream every link without a token.
    /// </summary>
    [Theory]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Forwarded-Host")]
    [InlineData("X-Forwarded-Proto")]
    [InlineData("X-Real-IP")]
    [InlineData("Forwarded")]
    [InlineData("Via")]
    [InlineData("X-Original-For")]
    [InlineData("X-Original-Host")]
    [InlineData("X-Original-Proto")]
    [InlineData("x-forwarded-for")]
    public void FromThisServer_WithAProxyHeader_IsFalse(string header)
    {
        var context = Request("127.0.0.1", "127.0.0.1");
        context.Request.Headers[header] = "203.0.113.9";

        var fromHere = LinkController.FromThisServer(context);

        Assert.False(fromHere);
    }

    /// <summary>
    /// The check refuses to run without a request.
    /// True: a caller's mistake is named at once.
    /// False: a missing request would be read as some address.
    /// </summary>
    [Fact]
    public void FromThisServer_NullContext_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LinkController.FromThisServer(null!));
    }

    /// <summary>
    /// A loopback request with no secret, or with another link's secret, is answered 403.
    /// True: a link's ID alone opens nothing, and one link's secret opens no other link.
    /// False: any process on this machine could stream every link by guessing IDs.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("other-link")]
    public async Task Get_MissingOrAnotherLinksSecret_Answers403(string? secretOf)
    {
        var files = Files();
        var key = string.IsNullOrEmpty(secretOf) ? secretOf : SecretOf(files, secretOf);
        _harness.Content.Upsert(Row(StorageMode.Link));
        _grants.Grant(LinkId, Guid.NewGuid());

        var result = await Controller(files).Get(LinkId, key);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    /// <summary>
    /// The right secret for a link this server holds no row for is answered 404.
    /// True: a link whose row was removed plays nothing.
    /// False: a stale link file would still stream after its row was forgotten.
    /// </summary>
    [Fact]
    public async Task Get_NoRowForTheLink_Answers404()
    {
        var files = Files();
        _grants.Grant(LinkId, Guid.NewGuid());

        var result = await Controller(files).Get(LinkId, SecretOf(files, LinkId));

        Assert.IsType<NotFoundResult>(result);
    }

    /// <summary>
    /// The right secret for a row that is a copy, not a link, is answered 404.
    /// True: the link route serves links only.
    /// False: a row turned into a copy would still stream from its source.
    /// </summary>
    [Fact]
    public async Task Get_RowIsACopy_Answers404()
    {
        var files = Files();
        _harness.Content.Upsert(Row(StorageMode.Copy));
        _grants.Grant(LinkId, Guid.NewGuid());

        var result = await Controller(files).Get(LinkId, SecretOf(files, LinkId));

        Assert.IsType<NotFoundResult>(result);
    }

    /// <summary>
    /// A link nobody here asked to play is answered 403, even with its secret.
    /// True: a read needs a user who started playback, or the probe.
    /// False: a leaked link URL would stream for nobody, with no user to hold to the source's limits.
    /// </summary>
    [Fact]
    public async Task Get_NobodyAskedToPlay_Answers403()
    {
        var files = Files();
        _harness.Content.Upsert(Row(StorageMode.Link));

        var result = await Controller(files).Get(LinkId, SecretOf(files, LinkId));

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Empty(_harness.PeerPaths);
    }

    /// <summary>
    /// A granted link is streamed from its source, and the source's bytes are the response.
    /// True: the player receives the file and the route reports nothing more to write.
    /// False: a link would not play, or the route would write a second result over the stream.
    /// </summary>
    [Fact]
    public async Task Get_GrantedLink_StreamsTheSourcesBytes()
    {
        var files = Files();
        _harness.AddPeer();
        _harness.Content.Upsert(Row(StorageMode.Link));
        _grants.Grant(LinkId, Guid.NewGuid());
        var sent = new byte[] { 1, 2, 3, 4, 5 };
        _harness.Peer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(sent) };
        var controller = Controller(files);
        var body = new MemoryStream();
        controller.Response.Body = body;

        var result = await controller.Get(LinkId, SecretOf(files, LinkId));

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(sent, body.ToArray());
    }

    /// <summary>
    /// A source that answers a granted link with an error has its status passed to the player.
    /// True: the player hears the source's own refusal.
    /// False: a refused stream would look like an empty file, or a success.
    /// </summary>
    [Fact]
    public async Task Get_SourceAnswersWithAnError_PassesTheStatusOn()
    {
        var files = Files();
        _harness.AddPeer();
        _harness.Content.Upsert(Row(StorageMode.Link));
        _grants.Grant(LinkId, Guid.NewGuid());
        _harness.Peer = _ => new HttpResponseMessage(HttpStatusCode.Gone);

        var result = await Controller(files).Get(LinkId, SecretOf(files, LinkId));

        Assert.Equal(StatusCodes.Status410Gone, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    /// <summary>
    /// The link route is anonymous, lives under Sync/Link, and its one action answers GET and HEAD only.
    /// True: Jellyfin's own callers, which send no token, can read a link and nothing can be written through it.
    /// False: links would not play, or the one anonymous route would accept writes.
    /// </summary>
    [Fact]
    public void Get_Route_IsAnonymousAndReadOnly()
    {
        var controller = typeof(LinkController);
        var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
            .ToList();

        var action = Assert.Single(actions);
        Assert.Equal(nameof(LinkController.Get), action.Name);
        Assert.NotNull(controller.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal(LinkFiles.Route, controller.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(new[] { "GET", "HEAD" }, action.GetCustomAttributes<HttpMethodAttribute>().SelectMany(a => a.HttpMethods).OrderBy(v => v, StringComparer.Ordinal).ToArray());
        Assert.All(action.GetCustomAttributes<HttpMethodAttribute>(), a => Assert.Equal("{linkId}", a.Template));
    }

    private static DefaultHttpContext Request(string? remote, string? local)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);
        context.Connection.LocalIpAddress = local is null ? null : IPAddress.Parse(local);
        return context;
    }

    private static string SecretOf(LinkFiles files, string linkId)
    {
        var url = files.UrlFor(linkId);
        return url[(url.IndexOf("?k=", StringComparison.Ordinal) + 3)..];
    }

    private static SyncItem Row(StorageMode mode) => new()
    {
        SourceLibraryId = "lib",
        LocalLibraryId = "local",
        SourceItemId = LinkId,
        SourcePath = "/src/Film/Film.mkv",
        SourceSize = 5,
        SourceCreateDate = DateTime.UtcNow,
        LocalPath = "/m/Film/Film.strm",
        Status = SyncStatus.Synced,
        StatusDate = DateTime.UtcNow,
        ServerKey = "peer",
        StorageMode = mode
    };

    private LinkFiles Files()
    {
        var (host, hostStub) = InterfaceStub.Create<IServerApplicationHost>();
        hostStub.Answer("get_HttpPort", _ => 8096);
        hostStub.Answer(nameof(IServerApplicationHost.GetLocalApiUrl), args => $"{args[1]}://{args[0]}:{args[2]}");
        var (paths, pathsStub) = InterfaceStub.Create<IApplicationPaths>();
        pathsStub.Answer("get_DataPath", _ => _harness.TempDir);
        return new LinkFiles(host, paths);
    }

    private LinkController Controller(LinkFiles files)
    {
        var streamer = new LinkStreamer(_harness.Config, _harness.Clients, _harness.Host, null!, _grants, new LinkHealth(), NullLogger<LinkStreamer>.Instance, LinkStreamer.StallTimeout);
        var controller = ControllerTestHarness.WithContext(new LinkController(files, _grants, _harness.Content, streamer, NullLogger<LinkController>.Instance));
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        controller.HttpContext.Connection.LocalIpAddress = IPAddress.Loopback;
        controller.HttpContext.Request.Method = "GET";
        return controller;
    }
}
