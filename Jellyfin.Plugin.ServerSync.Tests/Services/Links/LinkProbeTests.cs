using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// The link probe must queue a media probe only for this server's own new links, and never throw into Jellyfin's events or timers.
/// </summary>
public sealed class LinkProbeTests
{
    private const string LinkPath = "/media/Film.strm";
    private readonly Movie _link = new() { Id = Guid.NewGuid(), Path = LinkPath };
    private readonly LinkFakeRegistry _registry = new LinkFakeRegistry().Add(new LinkEntry(LinkPath, "/media/Film.mkv", "aaaa", "a"));
    private readonly LinkGrants _grants;
    private readonly ILibraryManager _library;
    private readonly InterfaceStub _libraryStub;
    private readonly InterfaceStub _providerStub;
    private readonly LinkProbe _probe;
    private DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private BaseItem? _found;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkProbeTests"/> class.
    /// </summary>
    public LinkProbeTests()
    {
        _found = _link;
        _grants = new LinkGrants(() => _now);
        (_library, _libraryStub) = InterfaceStub.Create<ILibraryManager>();
        _libraryStub.Answer(nameof(ILibraryManager.GetItemById), _ => _found);
        (var providers, _providerStub) = InterfaceStub.Create<IProviderManager>();
        _probe = new LinkProbe(_library, providers, null!, _registry, _grants, NullLogger<LinkProbe>.Instance, () => _now);
    }

    /// <summary>
    /// A link waits 20 seconds, is checked after two minutes, and is tried three times.
    /// True: a new link is probed soon after Jellyfin settles, and a dead one costs three tries.
    /// False: a probe would race Jellyfin's own refresh, or a broken link be probed without end.
    /// </summary>
    [Fact]
    public void Timings_AreTheDocumentedOnes()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), LinkProbe.Settle);
        Assert.Equal(TimeSpan.FromMinutes(2), LinkProbe.CheckAfter);
        Assert.Equal(3, LinkProbe.MaxAttempts);
    }

    /// <summary>
    /// Nothing waits for an item that is missing, has no path, is not a .strm, or is a .strm this server did not write.
    /// True: only this plugin's links are probed with its grant.
    /// False: a user's own .strm would be refreshed and granted, or a null item would throw into Jellyfin's event.
    /// </summary>
    /// <param name="path">The added item's path, or "none" for no item at all.</param>
    [Theory]
    [InlineData("none")]
    [InlineData(null)]
    [InlineData("/media/Film.mkv")]
    [InlineData("/media/Users Own.strm")]
    public void Track_NotThisServersLink_WaitsForNothing(string? path)
    {
        var item = path == "none" ? null : new Movie { Id = Guid.NewGuid(), Path = path };

        _probe.Track(item);

        Assert.Equal(0, _probe.Waiting);
    }

    /// <summary>
    /// A link tracked twice waits once.
    /// True: an item added and then updated is probed once.
    /// False: every library event would queue another probe of the same link.
    /// </summary>
    [Fact]
    public void Track_SameLinkTwice_WaitsOnce()
    {
        _probe.Track(_link);

        _probe.Track(_link);

        Assert.Equal(1, _probe.Waiting);
    }

    /// <summary>
    /// A registry that throws does not throw out of the tracking.
    /// True: a database hiccup during a scan leaves Jellyfin's item added event unharmed.
    /// False: one failed lookup would break library scans for every plugin.
    /// </summary>
    [Fact]
    public void Track_RegistryThrows_IsSwallowed()
    {
        var (links, linksStub) = InterfaceStub.Create<ILinkRegistry>();
        linksStub.Answer(nameof(ILinkRegistry.Find), _ => throw new InvalidOperationException("database is locked"));
        var (providers, _) = InterfaceStub.Create<IProviderManager>();
        var probe = new LinkProbe(_library, providers, null!, links, _grants, NullLogger<LinkProbe>.Instance, () => _now);

        var failure = Record.Exception(() => probe.Track(_link));

        Assert.Null(failure);
        Assert.Equal(0, probe.Waiting);
    }

    /// <summary>
    /// A link removed from the library before its turn is dropped without a probe or a grant.
    /// True: a link deleted during a scan costs nothing.
    /// False: a grant would be left open for a link that no longer exists.
    /// </summary>
    [Fact]
    public void Sweep_ItemGoneFromTheLibrary_IsDroppedUnprobed()
    {
        _probe.Track(_link);
        _found = null;
        _now += LinkProbe.Settle;

        _probe.Sweep();

        Assert.Equal(0, _probe.Waiting);
        Assert.Empty(_providerStub.Calls);
        Assert.Null(_grants.Use("aaaa"));
    }

    /// <summary>
    /// A link is probed exactly when its settle time is up, with a full refresh that probes remote content under a probe grant.
    /// True: Jellyfin reads the link's media info through the link route.
    /// False: the refresh would skip the remote probe and the link would stay without runtime.
    /// </summary>
    /// <param name="secondsLater">How long after the link was tracked.</param>
    /// <param name="probed">Whether the link is probed.</param>
    [Theory]
    [InlineData(19, false)]
    [InlineData(20, true)]
    public void Sweep_AtTheSettleTime_QueuesAProbingRefresh(int secondsLater, bool probed)
    {
        _probe.Track(_link);
        _now = _now.AddSeconds(secondsLater);

        _probe.Sweep();

        var refreshes = _providerStub.Calls.Where(c => c.Name == nameof(IProviderManager.QueueRefresh)).ToList();
        Assert.Equal(probed ? 1 : 0, refreshes.Count);
        Assert.Equal(1, _probe.Waiting);
        if (probed)
        {
            var options = (MetadataRefreshOptions)refreshes[0].Args[1]!;
            Assert.Equal(_link.Id, refreshes[0].Args[0]);
            Assert.True(options.EnableRemoteContentProbe);
            Assert.Equal(MetadataRefreshMode.FullRefresh, options.MetadataRefreshMode);
            Assert.True(_grants.Use("aaaa")!.Probe);
        }
    }

    /// <summary>
    /// A library that throws during the sweep does not throw out of it, and the next sweep still runs.
    /// True: a failure inside a timer callback cannot take the server down.
    /// False: one bad lookup would crash Jellyfin, or leave the sweep locked for good.
    /// </summary>
    [Fact]
    public void Sweep_LibraryThrows_IsSwallowedAndSweepsAgain()
    {
        _probe.Track(_link);
        _now += LinkProbe.Settle;
        _libraryStub.Answer(nameof(ILibraryManager.GetItemById), _ => throw new InvalidOperationException("library is busy"));
        var failure = Record.Exception(_probe.Sweep);
        _libraryStub.Answer(nameof(ILibraryManager.GetItemById), _ => _found);

        _probe.Sweep();

        Assert.Null(failure);
        Assert.Single(_providerStub.Calls, c => c.Name == nameof(IProviderManager.QueueRefresh));
    }

    /// <summary>
    /// Starting listens for added items, an added link then waits for its probe, and stopping stops listening.
    /// True: new links are noticed while the plugin runs and not after it stops.
    /// False: links would never be probed, or a stopped service would keep handling Jellyfin's events.
    /// </summary>
    [Fact]
    public async Task StartAsync_ItemAdded_TracksTheLink()
    {
        await _probe.StartAsync(CancellationToken.None);
        var handler = (EventHandler<ItemChangeEventArgs>)Assert.Single(_libraryStub.Calls, c => c.Name == "add_ItemAdded").Args[0]!;

        handler(_library, new ItemChangeEventArgs { Item = _link });
        await _probe.StopAsync(CancellationToken.None);
        _probe.Dispose();

        Assert.Equal(1, _probe.Waiting);
        Assert.Equal(handler, Assert.Single(_libraryStub.Calls, c => c.Name == "remove_ItemAdded").Args[0]);
    }
}
