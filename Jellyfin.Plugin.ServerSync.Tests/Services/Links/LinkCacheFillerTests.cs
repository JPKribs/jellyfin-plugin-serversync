using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// The link cache filler must queue each played link once while the cache is on, and keep the cache to the links that still qualify.
/// </summary>
public sealed class LinkCacheFillerTests : IDisposable
{
    private const string LinkA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string LinkB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string LinkPath = "/media/Film.strm";
    private readonly LinkTestTable _store = new();
    private readonly PluginConfiguration _config = new() { LinkCacheSizeGb = 1 };
    private readonly LinkFakeRegistry _registry = new LinkFakeRegistry().Add(new LinkEntry(LinkPath, "/media/Film.mkv", LinkA, "a"));
    private readonly InterfaceStub _sessionStub;
    private readonly LinkCache _cache;
    private readonly LinkCacheFiller _filler;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkCacheFillerTests"/> class.
    /// </summary>
    public LinkCacheFillerTests()
    {
        _config.Servers.Add(new SourceServer
        {
            Key = "a",
            Name = "Server A",
            Url = "http://a:8096",
            ApiKey = "k",
            LibraryMappings =
            {
                new LibraryMapping { SourceLibraryId = "Films", StorageMode = StorageMode.Link },
                new LibraryMapping { SourceLibraryId = "Off", IsEnabled = false },
                new LibraryMapping { SourceLibraryId = "Excluded", ExcludeFromLinkCache = true }
            }
        });
        var configManager = new FakeConfigManager(_config);
        _cache = new LinkCache(configManager, Path.Combine(_store.Folder, "cache"), NullLogger<LinkCache>.Instance, () => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var services = new ServiceCollection().AddSingleton(_store.Table).BuildServiceProvider();
        var (host, _) = InterfaceStub.Create<IServerApplicationHost>();
        (var sessions, _sessionStub) = InterfaceStub.Create<ISessionManager>();
        var (library, _) = InterfaceStub.Create<ILibraryManager>();
        _filler = new LinkCacheFiller(_cache, _registry, services, configManager, new FakeClientFactory(), host, sessions, library, NullLogger<LinkCacheFiller>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _filler.Dispose();
        _store.Dispose();
    }

    /// <summary>
    /// The filler and its static helpers refuse missing arguments.
    /// True: the mistake is named where it was made.
    /// False: a null would fail inside a playback event.
    /// </summary>
    [Fact]
    public void Members_NullArguments_Throw()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var config = new FakeConfigManager();
        var factory = new FakeClientFactory();
        var (host, _) = InterfaceStub.Create<IServerApplicationHost>();
        var (sessions, _) = InterfaceStub.Create<ISessionManager>();
        var (library, _) = InterfaceStub.Create<ILibraryManager>();
        var logger = NullLogger<LinkCacheFiller>.Instance;
        var episode = new Episode();

        Assert.Throws<ArgumentNullException>(() => new LinkCacheFiller(null!, _registry, services, config, factory, host, sessions, library, logger));
        Assert.Throws<ArgumentNullException>(() => new LinkCacheFiller(_cache, null!, services, config, factory, host, sessions, library, logger));
        Assert.Throws<ArgumentNullException>(() => new LinkCacheFiller(_cache, _registry, null!, config, factory, host, sessions, library, logger));
        Assert.Throws<ArgumentNullException>(() => new LinkCacheFiller(_cache, _registry, services, null!, factory, host, sessions, library, logger));
        Assert.Throws<ArgumentNullException>(() => new LinkCacheFiller(_cache, _registry, services, config, null!, host, sessions, library, logger));
        Assert.Throws<ArgumentNullException>(() => new LinkCacheFiller(_cache, _registry, services, config, factory, null!, sessions, library, logger));
        Assert.Throws<ArgumentNullException>(() => new LinkCacheFiller(_cache, _registry, services, config, factory, host, null!, library, logger));
        Assert.Throws<ArgumentNullException>(() => new LinkCacheFiller(_cache, _registry, services, config, factory, host, sessions, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new LinkCacheFiller(_cache, _registry, services, config, factory, host, sessions, library, null!));
        Assert.Throws<ArgumentNullException>(() => LinkCacheFiller.Ahead(null!, episode, 1));
        Assert.Throws<ArgumentNullException>(() => LinkCacheFiller.Ahead(new[] { episode }, null!, 1));
        Assert.Throws<ArgumentNullException>(() => LinkCacheFiller.MappingFor(null!, new SyncItem()));
        Assert.Throws<ArgumentNullException>(() => LinkCacheFiller.MappingFor(_config, null!));
    }

    /// <summary>
    /// Nothing is ahead of an episode with no season or number, or when no episodes are asked for.
    /// True: an episode Jellyfin could not number caches nothing but itself.
    /// False: a null number would throw, or a negative count would cache the whole show.
    /// </summary>
    /// <param name="season">The current episode's season.</param>
    /// <param name="number">The current episode's number.</param>
    /// <param name="count">How many episodes ahead are wanted.</param>
    [Theory]
    [InlineData(null, 1, 3)]
    [InlineData(1, null, 3)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 1, -1)]
    public void Ahead_NoPlaceOrNoCount_IsEmpty(int? season, int? number, int count)
    {
        var current = new Episode { Id = Guid.NewGuid(), ParentIndexNumber = season, IndexNumber = number };
        var all = new[] { current, new Episode { Id = Guid.NewGuid(), ParentIndexNumber = 1, IndexNumber = 2 } };

        var ahead = LinkCacheFiller.Ahead(all, current, count);

        Assert.Empty(ahead);
    }

    /// <summary>
    /// Specials, unnumbered episodes, and the episode itself under another entry are never ahead, and the last episode has nothing ahead.
    /// True: only real later episodes are cached ahead.
    /// False: a special or the episode being watched would be downloaded as the next one.
    /// </summary>
    [Fact]
    public void Ahead_SpecialsAndUnnumbered_AreSkipped()
    {
        var current = new Episode { Id = Guid.NewGuid(), ParentIndexNumber = 0, IndexNumber = 1 };
        var all = new[]
        {
            current,
            new Episode { Id = Guid.NewGuid(), ParentIndexNumber = 0, IndexNumber = 2 },
            new Episode { Id = Guid.NewGuid(), ParentIndexNumber = 1, IndexNumber = null },
            new Episode { Id = Guid.NewGuid(), ParentIndexNumber = null, IndexNumber = 5 },
            new Episode { Id = Guid.NewGuid(), ParentIndexNumber = 1, IndexNumber = 1 }
        };

        var ahead = LinkCacheFiller.Ahead(all, current, 10);
        var afterTheLast = LinkCacheFiller.Ahead(all, all[4], 10);

        Assert.Same(all[4], Assert.Single(ahead));
        Assert.Empty(afterTheLast);
    }

    /// <summary>
    /// A row's mapping is the enabled one for its library on its enabled server, matched in any case.
    /// True: a cached file is judged by the settings of the library it came through.
    /// False: a disabled server's or mapping's links would stay cached, or the wrong mapping's exclusion apply.
    /// </summary>
    /// <param name="serverKey">The row's server key.</param>
    /// <param name="libraryId">The row's source library id.</param>
    /// <param name="serverEnabled">Whether the server is enabled.</param>
    /// <param name="found">Whether a mapping is found.</param>
    [Theory]
    [InlineData("a", "Films", true, true)]
    [InlineData("A", "FILMS", true, true)]
    [InlineData("a", "Films", false, false)]
    [InlineData("a", "Off", true, false)]
    [InlineData("a", "Unknown", true, false)]
    [InlineData("other", "Films", true, false)]
    [InlineData(null, "Films", true, false)]
    public void MappingFor_NeedsAnEnabledServerAndMapping(string? serverKey, string libraryId, bool serverEnabled, bool found)
    {
        _config.Servers[0].IsEnabled = serverEnabled;
        var row = new SyncItem { ServerKey = serverKey, SourceLibraryId = libraryId };

        var mapping = LinkCacheFiller.MappingFor(_config, row);

        Assert.Equal(found, mapping is not null);
    }

    /// <summary>
    /// With the cache off nothing is queued.
    /// True: a server without the cache never downloads a played link.
    /// False: plays would fill a disk the user never offered.
    /// </summary>
    [Fact]
    public void Enqueue_CacheOff_QueuesNothing()
    {
        _config.LinkCacheSizeGb = 0;

        var queued = _filler.Enqueue(LinkA, CacheReason.Played, null);

        Assert.False(queued);
        Assert.Equal(0, _filler.Waiting);
    }

    /// <summary>
    /// A link is queued once however often it is asked for, in any case of its id, and another link beside it.
    /// True: two viewers of one film cause one download.
    /// False: the same file would be downloaded once per press of play.
    /// </summary>
    [Fact]
    public void Enqueue_SameLinkTwice_QueuesOnce()
    {
        var first = _filler.Enqueue(LinkA, CacheReason.Played, Guid.NewGuid());

        var again = _filler.Enqueue(LinkA.ToUpperInvariant(), CacheReason.Ahead, null);
        var other = _filler.Enqueue(LinkB, CacheReason.Ahead, null);

        Assert.True(first);
        Assert.False(again);
        Assert.True(other);
        Assert.Equal(2, _filler.Waiting);
    }

    /// <summary>
    /// A link the cache already holds is not queued.
    /// True: a cached film is played from disk and not fetched again.
    /// False: every play of a cached film would download it again.
    /// </summary>
    [Fact]
    public void Enqueue_AlreadyCached_QueuesNothing()
    {
        Put(LinkA, 10);

        var queued = _filler.Enqueue(LinkA, CacheReason.Played, null);

        Assert.False(queued);
        Assert.Equal(0, _filler.Waiting);
    }

    /// <summary>
    /// With the cache off the tidy up reads nothing.
    /// True: a server without the cache does no work for it.
    /// False: the table would be read on every run for a cache that is off.
    /// </summary>
    [Fact]
    public void Reconcile_CacheOff_DoesNothing()
    {
        _config.LinkCacheSizeGb = 0;
        var services = new ServiceCollection().BuildServiceProvider();
        var (host, _) = InterfaceStub.Create<IServerApplicationHost>();
        var (sessions, _) = InterfaceStub.Create<ISessionManager>();
        var (library, _) = InterfaceStub.Create<ILibraryManager>();
        using var filler = new LinkCacheFiller(_cache, _registry, services, new FakeConfigManager(_config), new FakeClientFactory(), host, sessions, library, NullLogger<LinkCacheFiller>.Instance);

        var failure = Record.Exception(filler.Reconcile);

        Assert.Null(failure);
    }

    /// <summary>
    /// A cached file is kept only while its link is still a link under a mapping that allows the cache, is not flagged, and has the same size.
    /// True: a film removed, excluded, found dead, or replaced on the source gives its disk space back.
    /// False: the cache would serve a stale file, or hold files for links that no longer exist.
    /// </summary>
    /// <param name="state">What became of the cached file's link.</param>
    /// <param name="kept">Whether the cached file is kept.</param>
    [Theory]
    [InlineData("healthy", true)]
    [InlineData("gone", false)]
    [InlineData("copy", false)]
    [InlineData("excluded", false)]
    [InlineData("flagged", false)]
    [InlineData("resized", false)]
    [InlineData("server-off", false)]
    public void Reconcile_KeepsOnlyLinksThatStillQualify(string state, bool kept)
    {
        Put(LinkA, 10);
        var row = LinkTestTable.Row(LinkA, _store.Media("Film.strm"), state == "copy" ? StorageMode.Copy : StorageMode.Link, serverKey: "a");
        row.SourceLibraryId = state == "excluded" ? "Excluded" : "Films";
        row.SourceSize = state == "resized" ? 11 : 10;
        if (state == "flagged")
        {
            row.Status = Jellyfin.Plugin.ServerSync.Models.Common.SyncStatus.Errored;
            row.Reason = LinkVerifier.Prefix + "the file is no longer on the source";
        }

        if (state != "gone")
        {
            _store.Table.Upsert(row);
        }

        _config.Servers[0].IsEnabled = state != "server-off";

        _filler.Reconcile();

        Assert.Equal(kept, _cache.Holds(LinkA));
    }

    /// <summary>
    /// Starting listens for playback starts and stops, and stopping stops listening and ends the service.
    /// True: played links are noticed while the plugin runs and not after.
    /// False: the cache would never fill, or a stopped service would keep handling Jellyfin's events.
    /// </summary>
    [Fact]
    public async Task StartAsync_ThenStop_SubscribesAndUnsubscribes()
    {
        await _filler.StartAsync(CancellationToken.None);

        await _filler.StopAsync(CancellationToken.None);

        var events = _sessionStub.Calls.Select(c => c.Name).ToList();
        Assert.Equal(new[] { "add_PlaybackStart", "add_PlaybackStopped", "remove_PlaybackStart", "remove_PlaybackStopped" }, events);
        Assert.True(_filler.ExecuteTask!.IsCompleted);
        Assert.False(_filler.ExecuteTask.IsFaulted);
    }

    /// <summary>
    /// A playback start of something that is not this server's link, or with no item at all, queues nothing and does not throw.
    /// True: ordinary playback costs the cache nothing and Jellyfin's event is never broken.
    /// False: every film played would be looked up or downloaded, or a bare event would throw into Jellyfin.
    /// </summary>
    /// <param name="path">The played item's path, or null for no item.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("/media/Film.mkv")]
    [InlineData("/media/Users Own.strm")]
    public async Task PlaybackStart_NotALink_QueuesNothing(string? path)
    {
        await _filler.StartAsync(CancellationToken.None);
        var onStart = (EventHandler<PlaybackProgressEventArgs>)_sessionStub.Calls.Single(c => c.Name == "add_PlaybackStart").Args[0]!;
        var played = new PlaybackProgressEventArgs { Item = path is null ? null : new Movie { Id = Guid.NewGuid(), Path = path } };

        var failure = Record.Exception(() => onStart(this, played));
        await _filler.StopAsync(CancellationToken.None);

        Assert.Null(failure);
        Assert.Equal(0, _filler.Waiting);
    }

    private void Put(string linkId, int size)
    {
        File.WriteAllBytes(_cache.PartPathFor(linkId, ".mkv"), new byte[size]);
        Assert.True(_cache.Complete(linkId, ".mkv", size, CacheReason.Played));
    }
}
