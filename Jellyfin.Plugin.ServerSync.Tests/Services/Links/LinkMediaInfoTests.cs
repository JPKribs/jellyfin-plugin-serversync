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
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// Link media info must take only bounded, path free details from a source, and leave the link to the probe whenever it cannot.
/// </summary>
public sealed class LinkMediaInfoTests
{
    private static readonly LinkEntry Link = new("/media/Film.strm", "/media/Film.mkv", "aaaa", "a", 1000);

    /// <summary>
    /// The service and its members refuse missing arguments.
    /// True: the mistake is named where it was made.
    /// False: a null would fail while a new link waits for its media info.
    /// </summary>
    [Fact]
    public async Task Members_NullArguments_Throw()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var (library, _) = InterfaceStub.Create<ILibraryManager>();
        var config = new FakeConfigManager();
        var factory = new FakeClientFactory();
        var (host, _) = InterfaceStub.Create<IServerApplicationHost>();
        var logger = NullLogger<LinkMediaInfo>.Instance;
        var info = new LinkMediaInfo(services, library, config, factory, host, logger);
        var movie = new Movie();

        Assert.Throws<ArgumentNullException>(() => new LinkMediaInfo(null!, library, config, factory, host, logger));
        Assert.Throws<ArgumentNullException>(() => new LinkMediaInfo(services, null!, config, factory, host, logger));
        Assert.Throws<ArgumentNullException>(() => new LinkMediaInfo(services, library, null!, factory, host, logger));
        Assert.Throws<ArgumentNullException>(() => new LinkMediaInfo(services, library, config, null!, host, logger));
        Assert.Throws<ArgumentNullException>(() => new LinkMediaInfo(services, library, config, factory, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new LinkMediaInfo(services, library, config, factory, host, null!));
        Assert.Throws<ArgumentNullException>(() => LinkMediaInfo.Lacks(null!));
        Assert.Throws<ArgumentNullException>(() => LinkMediaInfo.Merge(null!, new List<MediaStream>()));
        Assert.Throws<ArgumentNullException>(() => LinkMediaInfo.Merge(new List<MediaStream>(), null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => info.ShipAsync(null!, Link, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => info.ShipAsync(movie, null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => info.ApplyAsync(null!, new PeerLinkDescription(), null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => info.ApplyAsync(movie, null!, null, CancellationToken.None));
    }

    /// <summary>
    /// An item lacks media info while it has no runtime.
    /// True: a link with no runtime is asked about again, and one with a runtime is left alone.
    /// False: every link would be described on every check, or a blank one never filled.
    /// </summary>
    /// <param name="ticks">The item's runtime in ticks.</param>
    /// <param name="expected">Whether media info is lacking.</param>
    [Theory]
    [InlineData(null, true)]
    [InlineData(0L, true)]
    [InlineData(-1L, true)]
    [InlineData(1L, false)]
    [InlineData(long.MaxValue, false)]
    public void Lacks_FollowsTheRuntime(long? ticks, bool expected)
    {
        var item = new Movie { RunTimeTicks = ticks };

        var lacks = LinkMediaInfo.Lacks(item);

        Assert.Equal(expected, lacks);
    }

    /// <summary>
    /// A stream from the source loses its path and delivery address, and its long text is cut to 400 characters.
    /// True: nothing a source sends can point this server at a file or URL, or bloat the database.
    /// False: a source could plant a local path or an address in a link's streams.
    /// </summary>
    [Fact]
    public void Merge_ShippedStream_IsStrippedAndBounded()
    {
        var shipped = new MediaStream
        {
            Type = MediaStreamType.Video,
            Path = "/etc/passwd",
            DeliveryUrl = "http://attacker.invalid/x",
            IsExternalUrl = true,
            Codec = new string('c', 401),
            Language = new string('l', 400),
            Title = new string('t', 1000),
            Comment = null
        };

        var merged = Assert.Single(LinkMediaInfo.Merge(new List<MediaStream>(), new List<MediaStream> { shipped }));

        Assert.Null(merged.Path);
        Assert.Null(merged.DeliveryUrl);
        Assert.Null(merged.IsExternalUrl);
        Assert.Equal((400, 400, 400), (merged.Codec!.Length, merged.Language!.Length, merged.Title!.Length));
        Assert.Null(merged.Comment);
        Assert.Equal(0, merged.Index);
    }

    /// <summary>
    /// External and missing streams from the source are dropped, and no more than 200 are kept.
    /// True: a source's sidecar files are not claimed here, and a hostile answer cannot store thousands of streams.
    /// False: this server would list subtitles it does not hold, or a peer could flood a link's streams.
    /// </summary>
    [Fact]
    public void Merge_ShippedExternalsAndExcess_AreDropped()
    {
        var shipped = new List<MediaStream> { null!, new() { Type = MediaStreamType.Subtitle, IsExternal = true } };
        shipped.AddRange(Enumerable.Range(0, 250).Select(_ => new MediaStream { Type = MediaStreamType.Audio }));

        var merged = LinkMediaInfo.Merge(new List<MediaStream>(), shipped);

        Assert.Equal(200, merged.Count);
        Assert.All(merged, s => Assert.Equal(MediaStreamType.Audio, s.Type));
        Assert.Equal(Enumerable.Range(0, 200), merged.Select(s => s.Index));
    }

    /// <summary>
    /// With nothing from the source, only this server's external streams remain, and its embedded ones go.
    /// True: stale embedded streams from an old probe do not outlive a new description.
    /// False: a replaced file would keep the old file's audio tracks.
    /// </summary>
    [Fact]
    public void Merge_NothingShipped_KeepsOnlyLocalExternals()
    {
        var local = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Index = 0 },
            new() { Type = MediaStreamType.Subtitle, IsExternal = true, Path = "/media/Film.en.srt", Index = 1 }
        };

        var merged = Assert.Single(LinkMediaInfo.Merge(local, new List<MediaStream>()));

        Assert.Equal("/media/Film.en.srt", merged.Path);
        Assert.Equal(0, merged.Index);
    }

    /// <summary>
    /// A link whose server is not set up, or is disabled, takes nothing and builds no client.
    /// True: the link is left to the probe without a call.
    /// False: a disabled server would be asked for media info.
    /// </summary>
    /// <param name="present">Whether the server entry exists, disabled.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShipAsync_NoUsableServer_IsFalseWithoutAsking(bool present)
    {
        var factory = new FakeClientFactory();
        var server = Server();
        server.IsEnabled = false;

        var shipped = await Info(factory, present ? server : null).ShipAsync(new Movie(), Link, CancellationToken.None);

        Assert.False(shipped);
        Assert.Equal(0, factory.Requests);
    }

    /// <summary>
    /// A source that says the file is gone, sends nothing, does not know the route, fails, or cannot be reached leaves the link to the probe.
    /// True: every failure to take media info ends in a probe, never in a throw.
    /// False: one bad answer would break the queue that describes new links.
    /// </summary>
    /// <param name="answer">How the source answers.</param>
    [Theory]
    [InlineData("gone")]
    [InlineData("empty")]
    [InlineData("unknown-route")]
    [InlineData("error")]
    [InlineData("unreachable")]
    [InlineData("not-json")]
    public async Task ShipAsync_SourceGivesNothing_IsFalse(string answer)
    {
        using var handler = new StubHandler(_ => answer switch
        {
            "gone" => Json("{\"Items\":[{\"ItemId\":\"aaaa\",\"Exists\":false}]}"),
            "empty" => Json("{\"Items\":[]}"),
            "unknown-route" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "error" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            "not-json" => Json("<html>"),
            _ => throw new HttpRequestException("connection refused")
        });

        var shipped = await Info(new LinkStubClientFactory(handler), Server()).ShipAsync(new Movie(), Link, CancellationToken.None);

        Assert.False(shipped);
        Assert.Equal(1, handler.Requests);
    }

    /// <summary>
    /// The source is asked for the link's own id, with details, in this server's name.
    /// True: the answer describes the file the link streams.
    /// False: the source would describe another file, or send no details to apply.
    /// </summary>
    [Fact]
    public async Task ShipAsync_AsksForTheLinksDetails()
    {
        HttpRequestMessage? sent = null;
        using var handler = new StubHandler(request =>
        {
            sent = request;
            return Json("{\"Items\":[]}");
        });

        await Info(new LinkStubClientFactory(handler), Server()).ShipAsync(new Movie(), Link, CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal("/Sync/Peer/Links/Describe", sent!.RequestUri!.AbsolutePath);
        Assert.Equal("this-server", body.RootElement.GetProperty("ServerId").GetString());
        Assert.Equal("aaaa", body.RootElement.GetProperty("ItemIds")[0].GetString());
        Assert.True(body.RootElement.GetProperty("Details").GetBoolean());
    }

    /// <summary>
    /// A description is not applied to an item that is not video, or when it has no details, no runtime, or no streams.
    /// True: only a full description of a video replaces what a probe would find.
    /// False: a half empty answer would wipe a link's streams and mark it described.
    /// </summary>
    /// <param name="flaw">What is wrong with the item or the description.</param>
    [Theory]
    [InlineData("not-video")]
    [InlineData("no-details")]
    [InlineData("no-runtime")]
    [InlineData("zero-runtime")]
    [InlineData("no-streams")]
    [InlineData("null-streams")]
    [InlineData("no-stores")]
    public async Task ApplyAsync_NothingUsable_IsFalseAndChangesNothing(string flaw)
    {
        var description = new PeerLinkDescription
        {
            ItemId = "aaaa",
            Exists = true,
            HasDetails = flaw != "no-details",
            RunTimeTicks = flaw switch { "no-runtime" => null, "zero-runtime" => 0, _ => 5000 },
            Streams = flaw switch { "no-streams" => new List<MediaStream>(), "null-streams" => null!, _ => new List<MediaStream> { new() { Type = MediaStreamType.Video } } }
        };
        var movie = new Movie { Id = Guid.NewGuid() };

        var applied = await Info(new FakeClientFactory(), Server()).ApplyAsync(flaw == "not-video" ? new Audio() : movie, description, null, CancellationToken.None);

        Assert.False(applied);
        Assert.Null(movie.RunTimeTicks);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static SourceServer Server()
        => new() { Key = "a", Name = "Server A", Url = "http://a:8096", ApiKey = "k", Mode = ServerMode.Pull };

    private static LinkMediaInfo Info(ISourceServerClientFactory factory, SourceServer? server)
    {
        var config = new PluginConfiguration();
        if (server is not null)
        {
            config.Servers.Add(server);
        }

        var (library, _) = InterfaceStub.Create<ILibraryManager>();
        var (host, hostStub) = InterfaceStub.Create<IServerApplicationHost>();
        hostStub.Answer("get_SystemId", _ => "this-server");
        return new LinkMediaInfo(new ServiceCollection().BuildServiceProvider(), library, new FakeConfigManager(config), factory, host, NullLogger<LinkMediaInfo>.Instance);
    }
}
