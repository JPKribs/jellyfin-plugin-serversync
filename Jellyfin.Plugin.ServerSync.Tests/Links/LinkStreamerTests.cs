using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Links;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Links;

/// <summary>
/// The stream behind a link: a source that drops or stalls partway is picked up where it stopped, from
/// the same server or another that holds the file, and the probe's grant never reads a whole file.
/// </summary>
public class LinkStreamerTests
{
    private static readonly byte[] File = Enumerable.Range(0, 1000).Select(i => (byte)(i % 251)).ToArray();
    private static readonly LinkEntry Link = new("/media/Movies/Film.strm", "/media/Movies/Film.mkv", "aaaa", "a", 1000);

    /// <summary>
    /// A source that drops the stream partway is asked for the rest, and the player gets the whole file.
    /// True: a dropped connection between the servers does not interrupt playback.
    /// False: the player would see the stream end and have to start again.
    /// </summary>
    [Fact]
    public async Task Drop_Partway_IsPickedUpFromTheSameServer()
    {
        var peers = new Streams();
        peers["a"] = request => peers.Requests("a") == 1 ? Body(request, failAt: 400) : Body(request);
        var (context, body, aborted) = Request();

        var status = await Streamer(peers, new NoStandIn()).StreamAsync(context, Link, new LinkGrant(Guid.NewGuid(), false), "/src/Film.mkv");

        Assert.Equal(LinkStreamer.Written, status);
        Assert.Equal(File, body.ToArray());
        Assert.False(aborted());
        Assert.Equal("bytes=400-999", peers.Ranges["a"].Last());
    }

    /// <summary>
    /// A seek that drops partway asks for the rest of its own range, not the rest of the file.
    /// True: the player gets exactly the bytes it asked for.
    /// False: a resumed seek would send bytes past the range the player was promised.
    /// </summary>
    [Fact]
    public async Task Drop_InsideARange_ResumesWithinIt()
    {
        var peers = new Streams();
        peers["a"] = request => peers.Requests("a") == 1 ? Body(request, failAt: 50) : Body(request);
        var (context, body, _) = Request("bytes=200-499");

        await Streamer(peers, new NoStandIn()).StreamAsync(context, Link, new LinkGrant(Guid.NewGuid(), false), "/src/Film.mkv");

        Assert.Equal(File[200..500], body.ToArray());
        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("bytes=250-499", peers.Ranges["a"].Last());
    }

    /// <summary>
    /// When the server that dropped the stream cannot send the rest, another that holds the file does,
    /// and the playback's later reads go to it.
    /// True: a source that dies mid-film hands over without the player noticing.
    /// False: the player would see the stream end although another server holds the file.
    /// </summary>
    [Fact]
    public async Task Drop_ServerGone_AnotherSendsTheRest()
    {
        var peers = new Streams();
        peers["a"] = request => peers.Requests("a") == 1 ? Body(request, failAt: 300) : throw new HttpRequestException("connection refused");
        peers["b"] = request => Body(request);
        var grants = new LinkGrants();
        var user = Guid.NewGuid();
        grants.Grant(Link.LinkId, user);
        var (context, body, aborted) = Request();

        await Streamer(peers, new StandIn(new LinkRoute("b", "bbbb")), grants).StreamAsync(context, Link, new LinkGrant(user, false), "/src/Film.mkv");

        Assert.Equal(File, body.ToArray());
        Assert.False(aborted());
        Assert.Equal(new LinkRoute("b", "bbbb"), grants.RouteFor(Link.LinkId, user));
    }

    /// <summary>
    /// A source that stops sending without closing the connection counts as dropped after a short
    /// wait, and the rest is asked for again.
    /// True: a hung source does not hold the player until it gives up.
    /// False: the stream would hang for as long as the player waits.
    /// </summary>
    [Fact]
    public async Task Stall_IsPickedUpLikeADrop()
    {
        var peers = new Streams();
        peers["a"] = request => peers.Requests("a") == 1 ? Body(request, failAt: 600, hang: true) : Body(request);
        var (context, body, _) = Request();

        await Streamer(peers, new NoStandIn(), stall: TimeSpan.FromMilliseconds(100)).StreamAsync(context, Link, new LinkGrant(Guid.NewGuid(), false), "/src/Film.mkv");

        Assert.Equal(File, body.ToArray());
    }

    /// <summary>
    /// A server that answers the request for the rest with something else, such as the whole file, is
    /// not used, and the connection is closed.
    /// True: the player never gets bytes from the wrong place in the file.
    /// False: a resumed stream could repeat or skip part of the file.
    /// </summary>
    [Fact]
    public async Task Drop_RestNotOffered_ClosesTheConnection()
    {
        var peers = new Streams();
        peers["a"] = request => peers.Requests("a") == 1 ? Body(request, failAt: 400) : Body(new HttpRequestMessage());
        var (context, body, aborted) = Request();

        await Streamer(peers, new NoStandIn()).StreamAsync(context, Link, new LinkGrant(Guid.NewGuid(), false), "/src/Film.mkv");

        Assert.Equal(File[..400], body.ToArray());
        Assert.True(aborted());
    }

    /// <summary>
    /// A stream that keeps dropping is given up after a few tries.
    /// True: a broken source cannot keep one request retrying forever.
    /// False: a read could loop on a source that drops every attempt.
    /// </summary>
    [Fact]
    public async Task Drop_Repeated_GivesUp()
    {
        var peers = new Streams();
        peers["a"] = request => Body(request, failAt: 10);
        var (context, _, aborted) = Request();

        await Streamer(peers, new NoStandIn()).StreamAsync(context, Link, new LinkGrant(Guid.NewGuid(), false), "/src/Film.mkv");

        Assert.True(aborted());
        Assert.Equal(1 + LinkStreamer.MaxResumes, peers.Requests("a"));
    }

    /// <summary>
    /// A server that did not answer a moment ago is not waited on again while another holds the file.
    /// True: reads go straight to the server that can send them.
    /// False: every read would wait on the dead server first.
    /// </summary>
    [Fact]
    public async Task Open_ServerThatJustFailed_IsPassedOver()
    {
        var peers = new Streams();
        peers["a"] = _ => throw new HttpRequestException("connection refused");
        peers["b"] = request => Body(request);
        var health = new LinkHealth();
        health.Failed("a");
        var (context, body, _) = Request();

        await Streamer(peers, new StandIn(new LinkRoute("b", "bbbb")), health: health).StreamAsync(context, Link, new LinkGrant(Guid.NewGuid(), false), "/src/Film.mkv");

        Assert.Equal(File, body.ToArray());
        Assert.Equal(0, peers.Requests("a"));
    }

    /// <summary>
    /// The probe's grant ends once it has read as much as a probe may.
    /// True: nothing else on this server reads whole files from the source under the probe's grant.
    /// False: any job that reads a new link would pull the entire file, uncounted by the source.
    /// </summary>
    [Fact]
    public void ProbeGrant_EndsAtItsBudget()
    {
        var grants = new LinkGrants();
        grants.GrantProbe("link");

        Assert.True(grants.SpendProbe("link", LinkGrants.ProbeBudget - 1));
        Assert.NotNull(grants.Use("link"));
        Assert.True(grants.SpendProbe("link", 1));
        Assert.Null(grants.Use("link"));
        Assert.False(grants.SpendProbe("link", 1));

        grants.GrantProbe("link");
        Assert.NotNull(grants.Use("link"));
    }

    private static LinkStreamer Streamer(Streams peers, ILinkAccess access, LinkGrants? grants = null, LinkHealth? health = null, TimeSpan? stall = null)
    {
        var (host, hostStub) = InterfaceStub.Create<IServerApplicationHost>();
        hostStub.Answer("get_SystemId", _ => "local");
        var config = new PluginConfiguration { Servers = new[] { "a", "b" }.Select(key => new SourceServer { Key = key, Name = key, Url = $"http://{key}", ApiKey = "k", IsEnabled = true }).ToList() };
        return new LinkStreamer(new FakeConfig(config), new Factory(peers), host, access, grants ?? new LinkGrants(), health ?? new LinkHealth(), NullLogger<LinkStreamer>.Instance, stall ?? LinkStreamer.StallTimeout);
    }

    private static (HttpContext Context, MemoryStream Body, Func<bool> Aborted) Request(string? range = null)
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        var lifetime = new Lifetime();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        context.Request.Method = "GET";
        context.Response.Body = body;
        if (range is not null)
        {
            context.Request.Headers.Range = range;
        }

        return (context, body, () => lifetime.WasAborted);
    }

    // The file, or the range of it a request asks for, optionally breaking or hanging partway through.
    private static HttpResponseMessage Body(HttpRequestMessage request, int failAt = -1, bool hang = false)
    {
        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        var from = (int)(range?.From ?? 0);
        var to = (int)(range?.To ?? File.Length - 1);
        var content = new StreamContent(new Breaking(File[from..(to + 1)], failAt, hang));
        content.Headers.ContentLength = to - from + 1;
        if (range is not null)
        {
            content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, File.Length);
        }

        return new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = content };
    }

    private sealed class Streams : Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>>
    {
        public Dictionary<string, List<string?>> Ranges { get; } = new();

        public int Requests(string key) => Ranges.TryGetValue(key, out var seen) ? seen.Count : 0;
    }

    private sealed class Factory : ISourceServerClientFactory
    {
        private readonly Streams _peers;

        public Factory(Streams peers) => _peers = peers;

        public SourceServerClient Create(SourceServer server)
            => new(NullLogger<SourceServerClient>.Instance, new HttpClient(new Handler(_peers, server.Key)), server.Url, "k", "test", "0");
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Streams _peers;
        private readonly string _key;

        public Handler(Streams peers, string key)
        {
            _peers = peers;
            _key = key;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!_peers.Ranges.TryGetValue(_key, out var seen))
            {
                _peers.Ranges[_key] = seen = new List<string?>();
            }

            seen.Add(request.Headers.Range?.ToString());
            return Task.FromResult(_peers[_key](request));
        }
    }

    private sealed class Breaking : MemoryStream
    {
        private readonly int _failAt;
        private readonly bool _hang;

        public Breaking(byte[] data, int failAt, bool hang)
            : base(data)
        {
            _failAt = failAt;
            _hang = hang;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_failAt >= 0 && Position >= _failAt)
            {
                if (_hang)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                throw new IOException("the connection was reset");
            }

            var most = _failAt >= 0 ? (int)Math.Min(buffer.Length, _failAt - Position) : buffer.Length;
            return await base.ReadAsync(buffer[..most], cancellationToken);
        }
    }

    private sealed class Lifetime : IHttpRequestLifetimeFeature
    {
        public bool WasAborted { get; private set; }

        public CancellationToken RequestAborted { get; set; }

        public void Abort() => WasAborted = true;
    }

    private sealed class NoStandIn : ILinkAccess
    {
        public Task<LinkChoice> ChooseAsync(LinkEntry link, Guid? userId, IReadOnlyCollection<string>? skip, CancellationToken cancellationToken)
            => Task.FromResult(new LinkChoice(null, "nobody else holds the file"));
    }

    private sealed class StandIn : ILinkAccess
    {
        private readonly LinkRoute _route;

        public StandIn(LinkRoute route) => _route = route;

        public Task<LinkChoice> ChooseAsync(LinkEntry link, Guid? userId, IReadOnlyCollection<string>? skip, CancellationToken cancellationToken)
            => Task.FromResult(new LinkChoice(skip?.Contains(_route.ServerKey, StringComparer.OrdinalIgnoreCase) == true ? null : _route, null));
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
