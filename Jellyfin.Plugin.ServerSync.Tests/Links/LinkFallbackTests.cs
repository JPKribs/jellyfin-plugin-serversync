using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Links;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Links;

/// <summary>
/// Play-time fallback: a link streams from the server that won its file, and when that server is down,
/// full, or refuses, another server this one pulls from that maps the same folder stands in with the
/// same file.
/// </summary>
public class LinkFallbackTests
{
    private static readonly LinkEntry Link = new("/media/Movies/Film (2024)/Film (2024).strm", "/media/Movies/Film (2024)/Film (2024).mkv", "aaaa", "a", 1000);

    /// <summary>
    /// A local path maps back to the path the same file has on a server, in that server's own separator.
    /// True: the other server is asked for exactly the file the link stands in for.
    /// False: the other server would be asked for a path it does not have, and fallback would never find it.
    /// </summary>
    [Theory]
    [InlineData("/media/Movies/Film/Film.mkv", "/media/Movies", "/data/films", "/data/films/Film/Film.mkv")]
    [InlineData("/media/Movies/Film/Film.mkv", "/media/Movies/", "/data/films/", "/data/films/Film/Film.mkv")]
    [InlineData("/media/Movies/Film/Film.mkv", "/media/Movies", @"D:\Films", @"D:\Films\Film\Film.mkv")]
    [InlineData("/media/Movies2/Film.mkv", "/media/Movies", "/data/films", null)]
    [InlineData("/media/Shows/Film.mkv", "/media/Movies", "/data/films", null)]
    [InlineData("/media/Movies", "/media/Movies", "/data/films", null)]
    [InlineData("/media/Movies/../etc/passwd", "/media/Movies", "/data/films", null)]
    [InlineData("/media/Movies/Film.mkv", "", "/data/films", null)]
    public void SourcePathFor_MapsUnderTheRootOnly(string local, string localRoot, string sourceRoot, string? expected)
        => Assert.Equal(expected, LinkFallback.SourcePathFor(local, localRoot, sourceRoot));

    /// <summary>
    /// The alternates are the other pulled servers that map the link's folder, in priority order.
    /// True: fallback only tries servers that sync this folder here, highest priority first.
    /// False: fallback could ask the link's own server again, a server that is off or only receives, or one that maps another folder.
    /// </summary>
    [Fact]
    public void Alternates_AreOtherPulledServersThatMapTheFolder_InOrder()
    {
        var config = Config(
            Server("a", "/src/a"),
            Server("off", "/src/off", enabled: false),
            Server("push", "/src/push", mode: ServerMode.Push),
            Server("elsewhere", "/src/x", localRoot: "/media/Shows"),
            Server("unmapped", "/src/u", mappingOn: false),
            Server("c", @"C:\films"),
            Server("b", "/src/b"));

        var alternates = LinkFallback.Alternates(config, Link).ToList();

        Assert.Equal(new[] { "c", "b" }, alternates.Select(a => a.Server.Key));
        Assert.Equal(@"C:\films\Film (2024)\Film (2024).mkv", alternates[0].SourcePath);
        Assert.Equal("/src/b/Film (2024)/Film (2024).mkv", alternates[1].SourcePath);
    }

    /// <summary>
    /// When the link's own server allows it, nothing else is asked.
    /// True: a healthy link costs one check, as before fallback existed.
    /// False: every press of play would query every other server.
    /// </summary>
    [Fact]
    public async Task Choose_PrimaryAllows_UsesItAlone()
    {
        var peers = new Peers { ["a"] = _ => Allowed("aaaa"), ["b"] = _ => Allowed("bbbb") };
        var choice = await Access(peers, Server("a", "/src/a"), Server("b", "/src/b")).ChooseAsync(Link, Guid.NewGuid(), null, CancellationToken.None);

        Assert.Equal(new LinkRoute("a", "aaaa"), choice.Route);
        Assert.Equal(new[] { "a" }, peers.Asked.Select(r => r.Server));
    }

    /// <summary>
    /// When the link's own server refuses, the next server is asked by path and its id is used.
    /// True: a full or refusing source hands playback to another server that holds the same file.
    /// False: the user would be refused although another server could play it.
    /// </summary>
    [Fact]
    public async Task Choose_PrimaryRefuses_FallsBackByPath()
    {
        var peers = new Peers { ["a"] = _ => Refused("all link streams are in use"), ["b"] = _ => Allowed("bbbb") };
        var user = Guid.NewGuid();
        var choice = await Access(peers, Server("a", "/src/a"), Server("b", "/src/b")).ChooseAsync(Link, user, null, CancellationToken.None);

        Assert.Equal(new LinkRoute("b", "bbbb"), choice.Route);
        var asked = peers.Asked.Single(r => r.Server == "b").Request;
        Assert.Equal(string.Empty, asked.ItemId);
        Assert.Equal("/src/b/Film (2024)/Film (2024).mkv", asked.Path);
        Assert.Equal(user.ToString("N"), asked.UserId);
        Assert.False(asked.Probe);
    }

    /// <summary>
    /// A link's own server that cannot be reached is passed over.
    /// True: a source that is down does not stop playback when another holds the file.
    /// False: a dead source would block every link to it.
    /// </summary>
    [Fact]
    public async Task Choose_PrimaryDown_FallsBack()
    {
        var peers = new Peers { ["a"] = _ => throw new HttpRequestException("connection refused"), ["b"] = _ => Allowed("bbbb") };
        var choice = await Access(peers, Server("a", "/src/a"), Server("b", "/src/b")).ChooseAsync(Link, Guid.NewGuid(), null, CancellationToken.None);

        Assert.Equal(new LinkRoute("b", "bbbb"), choice.Route);
    }

    /// <summary>
    /// A server whose file at that path differs in size is not the same file, so it is not used.
    /// True: playback never switches to a different encode the link's media info does not describe.
    /// False: a file of another length or codec would play under the link's details.
    /// </summary>
    [Fact]
    public async Task Choose_DifferentSize_IsNotUsed()
    {
        var peers = new Peers { ["a"] = _ => Refused("the file is no longer on the source"), ["b"] = _ => Allowed("bbbb", size: 999), ["c"] = _ => Allowed("cccc", size: 1000) };
        var choice = await Access(peers, Server("a", "/src/a"), Server("b", "/src/b"), Server("c", "/src/c")).ChooseAsync(Link, Guid.NewGuid(), null, CancellationToken.None);

        Assert.Equal(new LinkRoute("c", "cccc"), choice.Route);
    }

    /// <summary>
    /// When nobody allows it, the refusal is the link's own server's reason, naming the others tried.
    /// True: the user sees why the source refused and that fallback was tried.
    /// False: the message would hide the real reason behind a fallback's.
    /// </summary>
    [Fact]
    public async Task Choose_AllRefuse_GivesPrimaryReasonAndOthersTried()
    {
        var peers = new Peers
        {
            ["a"] = _ => Refused("the source allows 2 link stream(s) at once and all are in use"),
            ["b"] = _ => Refused("the file is not on this server"),
            ["c"] = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("A request body with an ItemId is required") }
        };
        var choice = await Access(peers, Server("a", "/src/a"), Server("b", "/src/b", name: "Bee"), Server("c", "/src/c", name: "Old")).ChooseAsync(Link, Guid.NewGuid(), null, CancellationToken.None);

        Assert.Null(choice.Route);
        Assert.Equal("the source allows 2 link stream(s) at once and all are in use (also tried Bee, Old)", choice.Refusal);
    }

    /// <summary>
    /// A link whose own server is removed here still plays from another server that holds the file.
    /// True: removing a server does not strand its links while the next sync moves them.
    /// False: those links would fail until the next sync.
    /// </summary>
    [Fact]
    public async Task Choose_PrimaryRemoved_FallsBack()
    {
        var peers = new Peers { ["b"] = _ => Allowed("bbbb") };
        var choice = await Access(peers, Server("b", "/src/b")).ChooseAsync(Link, Guid.NewGuid(), null, CancellationToken.None);

        Assert.Equal(new LinkRoute("b", "bbbb"), choice.Route);
    }

    /// <summary>
    /// The probe asks with no user and says it is the probe, and a skipped server is not asked.
    /// True: a link whose source is down still gets its media info from a server that holds the file.
    /// False: the probe would be refused for want of a user, or ask the server that just failed.
    /// </summary>
    [Fact]
    public async Task Choose_Probe_SkipsFailedServer_AndAsksAsProbe()
    {
        var peers = new Peers { ["a"] = _ => Allowed("aaaa"), ["b"] = _ => Allowed("bbbb") };
        var choice = await Access(peers, Server("a", "/src/a"), Server("b", "/src/b")).ChooseAsync(Link, null, new[] { "A" }, CancellationToken.None);

        Assert.Equal(new LinkRoute("b", "bbbb"), choice.Route);
        var asked = Assert.Single(peers.Asked);
        Assert.True(asked.Request.Probe);
        Assert.Null(asked.Request.UserId);
    }

    /// <summary>
    /// An allowed answer from a fallback that names no item cannot be streamed, so it is passed over.
    /// True: a malformed answer never routes playback to an empty item id.
    /// False: the stream would ask the server for no item.
    /// </summary>
    [Fact]
    public async Task Choose_AllowedWithoutItem_IsPassedOver()
    {
        var peers = new Peers { ["a"] = _ => Refused("no"), ["b"] = _ => Allowed(null) };
        var choice = await Access(peers, Server("a", "/src/a"), Server("b", "/src/b")).ChooseAsync(Link, Guid.NewGuid(), null, CancellationToken.None);

        Assert.Null(choice.Route);
    }

    /// <summary>
    /// A server that did not answer is not asked again for a minute while another can play the file.
    /// True: only the first play after a server goes down waits on it.
    /// False: every play would wait out the dead server's timeout before falling back.
    /// </summary>
    [Fact]
    public async Task Choose_ServerThatJustFailed_IsPassedOver()
    {
        var now = DateTime.UtcNow;
        var health = new LinkHealth(() => now);
        var peers = new Peers { ["a"] = _ => throw new HttpRequestException("connection refused"), ["b"] = _ => Allowed("bbbb") };
        var access = Access(peers, health, LinkAccess.Hedge, Server("a", "/src/a"), Server("b", "/src/b"));

        await access.ChooseAsync(Link, Guid.NewGuid(), null, CancellationToken.None);
        peers.Asked.Clear();
        var choice = await access.ChooseAsync(Link, Guid.NewGuid(), null, CancellationToken.None);

        Assert.Equal(new LinkRoute("b", "bbbb"), choice.Route);
        Assert.Equal(new[] { "b" }, peers.Asked.Select(r => r.Server));

        now += LinkHealth.Remembered + TimeSpan.FromSeconds(1);
        peers["a"] = _ => Allowed("aaaa");
        Assert.Equal(new LinkRoute("a", "aaaa"), (await access.ChooseAsync(Link, Guid.NewGuid(), null, CancellationToken.None)).Route);
    }

    /// <summary>
    /// A server passed over for not answering is still asked when nobody else can play the file.
    /// True: a link with one source plays again the moment that source is back.
    /// False: a source that came back would stay refused for a minute.
    /// </summary>
    [Fact]
    public async Task Choose_PassedOverServer_IsAskedWhenNobodyElseCan()
    {
        var health = new LinkHealth();
        health.Failed("a");
        var peers = new Peers { ["a"] = _ => Allowed("aaaa"), ["b"] = _ => Refused("the file is not on this server") };
        var choice = await Access(peers, health, LinkAccess.Hedge, Server("a", "/src/a"), Server("b", "/src/b")).ChooseAsync(Link, Guid.NewGuid(), null, CancellationToken.None);

        Assert.Equal(new LinkRoute("a", "aaaa"), choice.Route);
        Assert.Equal(new[] { "b", "a" }, peers.Asked.Select(r => r.Server));
        Assert.False(health.IsDown("a"));
    }

    /// <summary>
    /// When the link's own server is slow to answer, the others are asked alongside it, and the first
    /// that allows the play is used.
    /// True: a server that hangs costs a short wait, not its whole timeout.
    /// False: play would wait the full timeout on a hung server before trying another.
    /// </summary>
    [Fact]
    public async Task Choose_SlowPrimary_UsesAnotherMeanwhile()
    {
        var peers = new Peers { ["a"] = _ => Allowed("aaaa"), ["b"] = _ => Allowed("bbbb") };
        peers.Delays["a"] = TimeSpan.FromSeconds(30);
        var started = DateTime.UtcNow;
        var choice = await Access(peers, new LinkHealth(), TimeSpan.FromMilliseconds(50), Server("a", "/src/a"), Server("b", "/src/b")).ChooseAsync(Link, Guid.NewGuid(), null, CancellationToken.None);

        Assert.Equal(new LinkRoute("b", "bbbb"), choice.Route);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The others are asked together, and the highest in priority that allows the play wins, however
    /// quickly a lower one answers.
    /// True: fallback keeps the Servers tab's order.
    /// False: whichever server answered first would be used.
    /// </summary>
    [Fact]
    public async Task Choose_Alternates_AskedTogether_PriorityWins()
    {
        var peers = new Peers { ["a"] = _ => Refused("no"), ["b"] = _ => Allowed("bbbb"), ["c"] = _ => Allowed("cccc") };
        peers.Delays["b"] = TimeSpan.FromMilliseconds(300);
        var choice = await Access(peers, Server("a", "/src/a"), Server("b", "/src/b"), Server("c", "/src/c")).ChooseAsync(Link, Guid.NewGuid(), null, CancellationToken.None);

        Assert.Equal(new LinkRoute("b", "bbbb"), choice.Route);
        Assert.Contains("c", peers.Asked.Select(r => r.Server));
    }

    /// <summary>
    /// A grant keeps the server it was routed to, and can be moved to another while live, never revived.
    /// True: the stream, its reports, and remote control all follow the server actually streaming.
    /// False: reads would go back to the failed server, or a lapsed grant would reopen the link.
    /// </summary>
    [Fact]
    public void Grants_KeepAndMoveTheRoute()
    {
        var now = DateTime.UtcNow;
        var grants = new LinkGrants(() => now);
        var user = Guid.NewGuid();
        var b = new LinkRoute("b", "bbbb");

        grants.Grant("link", user, b);
        Assert.Equal(b, grants.RouteFor("link", user));
        Assert.Equal(b, grants.Use("link")!.Route);

        var c = new LinkRoute("c", "cccc");
        grants.Reroute("link", user, c);
        Assert.Equal(c, grants.RouteFor("link", user));

        now += LinkGrants.InUse + TimeSpan.FromSeconds(1);
        grants.Reroute("link", user, b);
        Assert.Null(grants.RouteFor("link", user));
        Assert.Null(grants.Use("link"));
    }

    /// <summary>
    /// The probe's grant can be moved too, under the empty id.
    /// True: a probe that fell back keeps reading from the server that stood in.
    /// False: each of the probe's reads would retry the failed server first.
    /// </summary>
    [Fact]
    public void Grants_ProbeRoute_CanMove()
    {
        var grants = new LinkGrants();
        grants.GrantProbe("link");
        Assert.Null(grants.Use("link")!.Route);

        grants.Reroute("link", Guid.Empty, new LinkRoute("b", "bbbb"));

        Assert.Equal(new LinkRoute("b", "bbbb"), grants.Use("link")!.Route);
    }

    private static HttpResponseMessage Allowed(string? itemId, long size = 1000)
        => Json(new LinkStreamCheckResponse { Allowed = true, ItemId = itemId, Size = size });

    private static HttpResponseMessage Refused(string reason)
        => Json(new LinkStreamCheckResponse { Allowed = false, Reason = reason });

    private static HttpResponseMessage Json(object body)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static PluginConfiguration Config(params SourceServer[] servers) => new() { Servers = servers.ToList() };

    private static SourceServer Server(string key, string sourceRoot, bool enabled = true, ServerMode mode = ServerMode.Pull, string localRoot = "/media/Movies", bool mappingOn = true, string? name = null)
        => new()
        {
            Key = key,
            Name = name ?? key,
            Url = $"http://{key}",
            ApiKey = "k",
            IsEnabled = enabled,
            Mode = mode,
            LibraryMappings = new List<LibraryMapping> { new() { SourceRootPath = sourceRoot, LocalRootPath = localRoot, IsEnabled = mappingOn } }
        };

    private static LinkAccess Access(Peers peers, params SourceServer[] servers)
        => Access(peers, new LinkHealth(), LinkAccess.Hedge, servers);

    private static LinkAccess Access(Peers peers, LinkHealth health, TimeSpan hedge, params SourceServer[] servers)
    {
        var (host, hostStub) = InterfaceStub.Create<IServerApplicationHost>();
        hostStub.Answer("get_SystemId", _ => "local");
        return new LinkAccess(new FakeConfig(Config(servers)), new Factory(peers), host, NullLogger<LinkAccess>.Instance, health, hedge);
    }

    private sealed class Peers : Dictionary<string, Func<LinkStreamCheckRequest, HttpResponseMessage>>
    {
        public Peers()
            : base(StringComparer.OrdinalIgnoreCase)
        {
        }

        public List<(string Server, LinkStreamCheckRequest Request)> Asked { get; } = new();

        public Dictionary<string, TimeSpan> Delays { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Factory : ISourceServerClientFactory
    {
        private readonly Peers _peers;

        public Factory(Peers peers) => _peers = peers;

        public SourceServerClient Create(SourceServer server)
            => new(NullLogger<SourceServerClient>.Instance, new HttpClient(new Handler(_peers, server.Key)), server.Url, "k", "test", "0");
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Peers _peers;
        private readonly string _key;

        public Handler(Peers peers, string key)
        {
            _peers = peers;
            _key = key;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonSerializer.Deserialize<LinkStreamCheckRequest>(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            lock (_peers.Asked)
            {
                _peers.Asked.Add((_key, body));
            }

            if (_peers.Delays.TryGetValue(_key, out var delay))
            {
                await Task.Delay(delay, cancellationToken);
            }

            return _peers[_key](body);
        }
    }

    private sealed class FakeConfig : IPluginConfigurationManager
    {
        public FakeConfig(PluginConfiguration configuration) => Configuration = configuration;

        public PluginConfiguration Configuration { get; }

        public string LocalServerName => string.Empty;

        public string PluginVersion => "0.0.0-test";

        public string DecryptApiKey(string protectedKey) => protectedKey;

        public string ResolveRequestApiKey(string? requestApiKey, string? serverKey, string? requestUrl) => requestApiKey ?? string.Empty;

        public void SaveConfiguration()
        {
        }

        public string GetTempDownloadPath() => string.Empty;
    }
}
