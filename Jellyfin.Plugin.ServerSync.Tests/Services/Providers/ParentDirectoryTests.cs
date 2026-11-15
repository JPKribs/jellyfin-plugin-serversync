using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Providers;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Providers;

/// <summary>
/// The directory must list only sources whose Server Sync can answer a lookup, ask each one no more often than it needs to, and match an address to the entry it belongs to.
/// </summary>
public sealed class ParentDirectoryTests
{
    private readonly ProviderTestHarness _harness = new();

    /// <summary>
    /// The constructor refuses each missing dependency.
    /// True: a broken registration fails when Jellyfin builds its services.
    /// False: the first metadata refresh would fail with a null reference.
    /// </summary>
    [Fact]
    public void Constructor_MissingDependency_Throws()
    {
        var host = InterfaceStub.Create<IServerApplicationHost>().Proxy;
        var logger = NullLogger<ParentDirectory>.Instance;

        Assert.Throws<ArgumentNullException>(() => new ParentDirectory(null!, _harness.Factory, host, logger));
        Assert.Throws<ArgumentNullException>(() => new ParentDirectory(_harness.Config, null!, host, logger));
        Assert.Throws<ArgumentNullException>(() => new ParentDirectory(_harness.Config, _harness.Factory, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new ParentDirectory(_harness.Config, _harness.Factory, host, null!));
    }

    /// <summary>
    /// With no server to pull from, nothing is listed and nothing is asked.
    /// True: a server that only pushes makes no outbound call on a metadata refresh.
    /// False: every library scan would call servers this one does not read from.
    /// </summary>
    [Fact]
    public async Task ParentsAsync_NoPullServers_ListsNoneAndAsksNobody()
    {
        _harness.AddSource("a");
        _harness.Entry("a").Mode = ServerMode.Push;
        _harness.AddSource("b");
        _harness.Entry("b").IsEnabled = false;

        var parents = await _harness.Directory.ParentsAsync(CancellationToken.None);

        Assert.Empty(parents);
        Assert.Empty(_harness.Factory.Created);
    }

    /// <summary>
    /// Sources that answer as a compatible Server Sync are listed in their configured order.
    /// True: the highest ranked source is asked first.
    /// False: metadata would come from whichever source happened to answer first.
    /// </summary>
    [Fact]
    public async Task ParentsAsync_CompatibleSources_AreListedInOrder()
    {
        _harness.AddSource("first");
        _harness.AddSource("second");
        _harness.Entry("second").Mode = ServerMode.Sync;

        var parents = await _harness.Directory.ParentsAsync(CancellationToken.None);

        Assert.Equal(new[] { "first", "second" }, parents.Select(p => p.Key));
    }

    /// <summary>
    /// A source without Server Sync, on a protocol this build cannot speak, reporting this server's own id, or out of reach is left out.
    /// True: only a source that can really answer a lookup is asked for metadata.
    /// False: this server would ask itself, or every refresh would wait on a source that cannot answer.
    /// </summary>
    [Theory]
    [InlineData("not-installed")]
    [InlineData("protocol-too-new")]
    [InlineData("another-version-without-a-protocol")]
    [InlineData("this-server-itself")]
    [InlineData("server-error")]
    [InlineData("connection-refused")]
    [InlineData("url-refused")]
    public async Task ParentsAsync_SourceThatCannotAnswer_IsLeftOut(string fault)
    {
        var good = _harness.AddSource("good");
        var bad = _harness.AddSource("bad");
        _ = fault switch
        {
            "not-installed" => bad.OnStatus("/Sync/Peer/Capabilities", HttpStatusCode.NotFound),
            "protocol-too-new" => bad.OnJson("/Sync/Peer/Capabilities", "{\"ServerId\":\"bad-id\",\"ProtocolVersion\":99,\"MinProtocolVersion\":99}"),
            "another-version-without-a-protocol" => bad.OnJson("/Sync/Peer/Capabilities", "{\"ServerId\":\"bad-id\",\"PluginVersion\":\"1.0.0.0\"}"),
            "this-server-itself" => bad.OnJson("/Sync/Peer/Capabilities", ProviderTestHarness.Capabilities(ProviderTestHarness.ThisServerId.ToUpperInvariant())),
            "server-error" => bad.OnStatus("/Sync/Peer/Capabilities", HttpStatusCode.InternalServerError),
            "connection-refused" => bad.OnThrow("/Sync/Peer/Capabilities", new HttpRequestException("refused")),
            _ => bad
        };
        if (fault == "url-refused")
        {
            _harness.Factory.Handlers.Remove("bad");
        }

        var parents = await _harness.Directory.ParentsAsync(CancellationToken.None);

        Assert.Equal("good", Assert.Single(parents).Key);
        Assert.Single(good.Seen);
    }

    /// <summary>
    /// An answer is remembered, so a second listing asks nobody.
    /// True: a library scan of thousands of items asks each source once.
    /// False: every item would cost a capabilities call to every source.
    /// </summary>
    [Fact]
    public async Task ParentsAsync_AskedAgain_UsesTheRememberedAnswer()
    {
        var server = _harness.AddSource("a");
        var missing = _harness.AddSource("b").OnStatus("/Sync/Peer/Capabilities", HttpStatusCode.NotFound);

        await _harness.Directory.ParentsAsync(CancellationToken.None);
        var parents = await _harness.Directory.ParentsAsync(CancellationToken.None);

        Assert.Equal("a", Assert.Single(parents).Key);
        Assert.Single(server.Seen);
        Assert.Single(missing.Seen);
    }

    /// <summary>
    /// Forgetting, or a changed Server URL, asks the source again.
    /// True: saving the settings takes effect on the next refresh.
    /// False: an entry pointed at a new server would be judged by the old server's answer for minutes.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ParentsAsync_ForgottenOrUrlChanged_AsksAgain(bool forget)
    {
        var server = _harness.AddSource("a");
        await _harness.Directory.ParentsAsync(CancellationToken.None);

        if (forget)
        {
            _harness.Directory.Forget();
        }
        else
        {
            _harness.Entry("a").Url = "https://moved.example";
        }

        await _harness.Directory.ParentsAsync(CancellationToken.None);

        Assert.Equal(2, server.Seen.Count);
    }

    /// <summary>
    /// A cancelled wait throws while a source has not answered.
    /// True: a cancelled refresh does not wait out a silent source.
    /// False: a cancelled refresh would hang until the source's own deadline.
    /// </summary>
    [Fact]
    public async Task ParentsAsync_CancelledWhileASourceIsSilent_Throws()
    {
        _harness.AddSource("a");
        using var silent = new ScriptedHandler(async token =>
        {
            await new TaskCompletionSource<HttpResponseMessage>().Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        _harness.Factory.Handlers["a"] = silent;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _harness.Directory.ParentsAsync(cancelled.Token));
    }

    /// <summary>
    /// An address belongs to the pull server whose URL it starts with at a path boundary, and to the longest such URL.
    /// True: an image address is fetched with the key of the server that holds it.
    /// False: a look-alike host would be sent a real server's API Key, or a server under a sub path would get its neighbor's key.
    /// </summary>
    [Theory]
    [InlineData("https://a.example/Items/1/Images/Primary", "a")]
    [InlineData("HTTPS://A.EXAMPLE/Items/1", "a")]
    [InlineData("https://a.example/jellyfin/Items/1", "sub")]
    [InlineData("https://a.example.evil.test/Items/1", null)]
    [InlineData("https://a.example", null)]
    [InlineData("https://push.example/Items/1", null)]
    [InlineData("https://other.example/Items/1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FindByUrl_Address_MatchesTheEntryItBelongsTo(string? url, string? expectedKey)
    {
        _harness.AddSource("a");
        _harness.AddSource("sub");
        _harness.Entry("sub").Url = "https://a.example/jellyfin/";
        _harness.AddSource("push");
        _harness.Entry("push").Mode = ServerMode.Push;
        _harness.AddSource("blank");
        _harness.Entry("blank").Url = " ";

        var entry = _harness.Directory.FindByUrl(url!);

        Assert.Equal(expectedKey, entry?.Key);
    }

    /// <summary>
    /// The version is the plugin's own.
    /// True: a "no match" log line names the version this server runs.
    /// False: the log would name a version no server has.
    /// </summary>
    [Fact]
    public void Version_IsThePluginVersion()
    {
        Assert.Equal(_harness.Config.PluginVersion, _harness.Directory.Version);
    }
}
