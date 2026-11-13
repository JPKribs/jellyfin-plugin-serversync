using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Links;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Links;

/// <summary>
/// The link cache: what it holds, what it drops and in which order, what it serves and hands to the
/// library, and which links Always Keep copies instead.
/// </summary>
public sealed class LinkCacheTests : IDisposable
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string C = "cccccccccccccccccccccccccccccccc";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "serversync-cache-" + Guid.NewGuid().ToString("N"));
    private readonly PluginConfiguration _config = new() { LinkCacheSizeGb = 1, LinkCacheRetentionDays = 0 };
    private DateTime _now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

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
    /// A finished download of the right size is held and found, and one of the wrong size is thrown away.
    /// True: only a whole file is ever played from the cache.
    /// False: a download cut short would play as a broken film.
    /// </summary>
    [Fact]
    public void Complete_TakesAWholeFile_AndRefusesAShortOne()
    {
        var cache = Cache();
        Put(cache, A, 100);
        File.WriteAllBytes(cache.PartPathFor(B, ".mkv"), new byte[50]);

        Assert.False(cache.Complete(B, ".mkv", 100, CacheReason.Played));

        Assert.Equal(Path.Combine(_dir, A + ".mkv"), cache.PathFor(A));
        Assert.Null(cache.PathFor(B));
        Assert.False(File.Exists(cache.PartPathFor(B, ".mkv")));
        Assert.Equal(100, cache.UsedBytes());
    }

    /// <summary>
    /// The cache survives a restart, and with the cache off nothing is served.
    /// True: cached files are still there after Jellyfin restarts, and turning the cache off stops it at once.
    /// False: every restart would download everything again, or a switched off cache would still play.
    /// </summary>
    [Fact]
    public void Index_SurvivesARestart_AndOffServesNothing()
    {
        Put(Cache(), A, 100);

        var reopened = Cache();
        Assert.NotNull(reopened.PathFor(A));

        _config.LinkCacheSizeGb = 0;
        Assert.Null(reopened.PathFor(A));
    }

    /// <summary>
    /// A file removed behind the cache's back is forgotten rather than served.
    /// True: a link whose cached file is gone streams from its source again.
    /// False: the link would fail to play until the cache was emptied.
    /// </summary>
    [Fact]
    public void PathFor_FileGone_IsForgotten()
    {
        var cache = Cache();
        Put(cache, A, 100);
        File.Delete(Path.Combine(_dir, A + ".mkv"));

        Assert.Null(cache.PathFor(A));
        Assert.Empty(cache.List());
    }

    /// <summary>
    /// Room is made in the order the setting names.
    /// True: the administrator decides what goes first.
    /// False: the wrong file would be dropped.
    /// </summary>
    [Theory]
    [InlineData(CacheRemovalOrder.LastPlayed, B)]
    [InlineData(CacheRemovalOrder.Oldest, A)]
    [InlineData(CacheRemovalOrder.Newest, C)]
    [InlineData(CacheRemovalOrder.Largest, C)]
    public void MakeRoom_DropsInTheChosenOrder(CacheRemovalOrder order, string firstToGo)
    {
        var cache = Cache();
        Put(cache, A, 100);
        _now += TimeSpan.FromHours(1);
        Put(cache, B, 100);
        _now += TimeSpan.FromHours(1);
        Put(cache, C, 300);
        _now += TimeSpan.FromHours(1);
        cache.Played(A);
        _now += TimeSpan.FromHours(1);
        cache.Played(C);
        _config.LinkCacheRemovalOrder = order;

        Assert.Equal(firstToGo, LinkCache.RemovalOrder(cache.List(), order).First().LinkId);
    }

    /// <summary>
    /// Making room drops files until the new one fits, keeps what is being played, and says so when
    /// what is left may not be dropped.
    /// True: the cache never passes its size, and never deletes the film someone is watching.
    /// False: the cache would grow past its limit, or cut off playback.
    /// </summary>
    [Fact]
    public void MakeRoom_FitsTheLimit_AndKeepsWhatIsPlaying()
    {
        var gigabyte = 1024L * 1024 * 1024;
        var cache = Cache();
        PutSized(cache, A, gigabyte / 2);
        _now += TimeSpan.FromHours(1);
        PutSized(cache, B, gigabyte / 4);

        Assert.True(cache.MakeRoom(gigabyte / 4));
        Assert.Equal(2, cache.List().Count);

        Assert.True(cache.MakeRoom(gigabyte / 2, new HashSet<string> { A }));
        Assert.Equal(new[] { A }, cache.List().Select(e => e.LinkId));

        Assert.False(cache.MakeRoom(gigabyte, new HashSet<string> { A }));
        Assert.NotNull(cache.List().SingleOrDefault(e => e.LinkId == A));
    }

    /// <summary>
    /// Retention drops a file not played for the set number of days, counted from when it was cached
    /// if it was never played.
    /// True: the cache clears itself of what nobody watches.
    /// False: files would sit in the cache for good.
    /// </summary>
    [Fact]
    public void Retention_DropsWhatWasNotPlayed()
    {
        _config.LinkCacheRetentionDays = 7;
        var cache = Cache();
        Put(cache, A, 100);
        Put(cache, B, 100);
        _now += TimeSpan.FromDays(5);
        cache.Played(B);
        _now += TimeSpan.FromDays(3);

        cache.MakeRoom(0);

        Assert.Equal(new[] { B }, cache.List().Select(e => e.LinkId));
    }

    /// <summary>
    /// Tidying at startup clears downloads cut short and files the index does not list, and keeps
    /// whole cached files. Emptying the cache removes its own files and leaves anything else in the folder.
    /// True: a restart keeps the cache, and a folder shared by mistake loses nothing that is not the cache's.
    /// False: half files would pile up, or emptying the cache could delete someone's own files.
    /// </summary>
    [Fact]
    public void Tidy_KeepsWholeFiles_AndWipeLeavesOtherFiles()
    {
        var cache = Cache();
        Put(cache, A, 100);
        File.WriteAllBytes(cache.PartPathFor(B, ".mkv"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_dir, C + ".mkv"), new byte[10]);
        var mine = Path.Combine(_dir, "Holiday.mkv");
        File.WriteAllBytes(mine, new byte[10]);

        cache.Tidy();

        Assert.NotNull(cache.PathFor(A));
        Assert.False(File.Exists(cache.PartPathFor(B, ".mkv")));
        Assert.False(File.Exists(Path.Combine(_dir, C + ".mkv")));
        Assert.True(File.Exists(mine));

        cache.Wipe();

        Assert.True(File.Exists(mine));
        Assert.Null(cache.PathFor(A));
        Assert.Equal(new[] { "Holiday.mkv" }, Directory.EnumerateFiles(_dir).Select(Path.GetFileName));
    }

    /// <summary>
    /// A cached link is served from the local file, with byte ranges, and the source is never asked.
    /// True: a cached link plays while its source is down, and seeking works.
    /// False: the cache would hold files that still streamed from the source.
    /// </summary>
    [Fact]
    public async Task Streamer_ServesACachedLinkFromHere()
    {
        var cache = Cache();
        var data = Enumerable.Range(0, 1000).Select(i => (byte)(i % 251)).ToArray();
        File.WriteAllBytes(cache.PartPathFor(A, ".mkv"), data);
        Assert.True(cache.Complete(A, ".mkv", data.Length, CacheReason.Played));
        var streamer = new LinkStreamer(new FakeConfig(_config), null!, null!, null!, new LinkGrants(), new LinkHealth(), NullLogger<LinkStreamer>.Instance, LinkStreamer.StallTimeout, cache);
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        var body = new MemoryStream();
        context.Request.Method = "GET";
        context.Request.Headers.Range = "bytes=100-199";
        context.Response.Body = body;

        var status = await streamer.StreamAsync(context, new LinkEntry("/m/Film.strm", "/m/Film.mkv", A, "gone"), new LinkGrant(Guid.NewGuid(), false), "/src/Film.mkv");

        Assert.Equal(LinkStreamer.Written, status);
        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal(data[100..200], body.ToArray());
    }

    /// <summary>
    /// The episodes ahead are the next ones in order, across a season's end, without specials.
    /// True: the episode a viewer reaches next is the one cached ahead of them.
    /// False: the wrong episodes would be cached, and the next press of play would stream.
    /// </summary>
    [Fact]
    public void Ahead_TakesTheNextEpisodesInOrder()
    {
        Episode Ep(int season, int number) => new() { Id = Guid.NewGuid(), ParentIndexNumber = season, IndexNumber = number };
        var current = Ep(1, 9);
        var all = new[] { Ep(2, 2), Ep(0, 1), Ep(1, 8), current, Ep(2, 1), Ep(1, 10), Ep(2, 3) };

        var ahead = LinkCacheFiller.Ahead(all, current, 3);

        Assert.Equal(new[] { (1, 10), (2, 1), (2, 2) }, ahead.Select(e => (e.ParentIndexNumber!.Value, e.IndexNumber!.Value)));
        Assert.Empty(LinkCacheFiller.Ahead(all, current, 0));
    }

    /// <summary>
    /// A cached file moves out of the cache to where the link's copy belongs, and is forgotten by the
    /// cache. Nothing moves when the size is wrong or something is already there.
    /// True: keeping a cached link puts the file in the library without downloading it again.
    /// False: the copy would download again, a short file would become the copy, or a file in the library would be replaced.
    /// </summary>
    [Fact]
    public void MoveTo_PutsTheCachedFileInPlace()
    {
        var cache = Cache();
        Put(cache, A, 100);
        Put(cache, B, 100);
        var library = Path.Combine(_dir, "library", "Film (2020)");
        var copy = Path.Combine(library, "Film (2020).mkv");

        Assert.False(cache.MoveTo(A, copy, 99));
        Assert.True(cache.MoveTo(A, copy, 100));
        Assert.Equal(100, new FileInfo(copy).Length);
        Assert.Null(cache.PathFor(A));

        Assert.False(cache.MoveTo(B, copy, 100));
        Assert.NotNull(cache.PathFor(B));
        Assert.False(cache.MoveTo(C, Path.Combine(library, "Other.mkv"), 100));
    }

    /// <summary>
    /// A file under an item picked to always keep is copied, everything else in a Link mapping is
    /// linked, and a Copy mapping copies whatever is picked.
    /// True: picking a show copies every episode of it, and nothing else, whether or not the library is cached.
    /// False: picked shows would stay links, or a show sharing a name prefix would be copied too.
    /// </summary>
    [Fact]
    public void AlwaysKeep_CopiesWhatIsUnderPickedItems()
    {
        var mapping = new LibraryMapping
        {
            SourceRootPath = "/src/shows",
            StorageMode = StorageMode.Link,
            ExcludeFromLinkCache = true,
            PermanentItems = new List<FilteredItem> { new() { ItemId = "s", Path = "/src/shows/Kept Show" } }
        };

        Assert.Equal(StorageMode.Copy, AlwaysKeep.StorageFor(mapping, "/src/shows/Kept Show/Season 01/E01.mkv"));
        Assert.Equal(StorageMode.Link, AlwaysKeep.StorageFor(mapping, "/src/shows/Kept Show 2/Season 01/E01.mkv"));
        Assert.Equal(StorageMode.Link, AlwaysKeep.StorageFor(mapping, "/src/shows/Other/E01.mkv"));
        Assert.Equal(StorageMode.Link, AlwaysKeep.StorageFor(mapping, null));

        mapping.StorageMode = StorageMode.Copy;
        Assert.False(AlwaysKeep.Covers(mapping, "/src/shows/Kept Show/Season 01/E01.mkv"));
        Assert.Equal(StorageMode.Copy, AlwaysKeep.StorageFor(mapping, "/src/shows/Other/E01.mkv"));
    }

    /// <summary>
    /// Picking a film copies every version of it.
    /// True: each version a user might play is a local file.
    /// False: only the version picked would be copied, and the others left as links.
    /// </summary>
    [Fact]
    public void AlwaysKeep_CopiesEveryVersionOfAPickedFilm()
    {
        var mapping = new LibraryMapping
        {
            SourceRootPath = "/src/m",
            StorageMode = StorageMode.Link,
            PermanentItems = new List<FilteredItem> { new() { ItemId = "f", Path = "/src/m/Film (2020)/Film (2020) - 4K.mkv" } }
        };

        Assert.True(AlwaysKeep.Covers(mapping, "/src/m/Film (2020)/Film (2020) - 1080p.mkv"));
        Assert.False(AlwaysKeep.Covers(mapping, "/src/m/Film (2021)/Film (2021).mkv"));
    }

    /// <summary>
    /// Two files are versions of one film when they share its folder and are both named after it.
    /// True: every version of a picked film is copied, and nothing else in its library.
    /// False: only the film's first version would be copied, or another film would be.
    /// </summary>
    [Theory]
    [InlineData("/src/m/Film (2020)/Film (2020) - 4K.mkv", "/src/m/Film (2020)/Film (2020) - 1080p.mkv", true)]
    [InlineData(@"D:\m\Film (2020)\Film (2020) - 4K.mkv", @"D:\m\Film (2020)\Film (2020).mp4", true)]
    [InlineData("/src/m/Film (2020)/Film (2020) - 4K.mkv", "/src/m/Film (2021)/Film (2021).mkv", false)]
    [InlineData("/src/m/Loose A.mkv", "/src/m/Loose B.mkv", false)]
    [InlineData("/src/shows/Show", "/src/shows/Show/Season 01/E01.mkv", false)]
    public void SameFilm_IsAnotherVersionInItsFolder(string picked, string other, bool expected)
        => Assert.Equal(expected, LinkCacheFiller.SameFilm(picked, other));

    /// <summary>
    /// Emptying a cache folder from outside the cache, as saving the settings does, is seen by the
    /// cache, which forgets what it held there.
    /// True: turning the cache off and on again shows it empty, with nothing counted that is not there.
    /// False: the cache would count files that were deleted, and make room for nothing.
    /// </summary>
    [Fact]
    public void WipeFolder_IsSeenByTheCache()
    {
        var cache = Cache();
        Put(cache, A, 100);

        LinkCache.WipeFolder(_dir);

        Assert.Empty(cache.List());
        Assert.Equal(0, cache.UsedBytes());
    }

    /// <summary>
    /// A location the administrator chose gets a folder of the cache's own inside it, and a location
    /// that is not a full path is not used.
    /// True: the cache never shares a folder with other files, wherever it is pointed.
    /// False: pointing the cache at a folder in use could delete files there that look like its own.
    /// </summary>
    [Fact]
    public void Root_IsAFolderOfItsOwn()
    {
        var chosen = Path.Combine(Path.GetTempPath(), "somewhere");

        Assert.Equal(Path.Combine(chosen, LinkCache.DefaultFolder), LinkCache.RootFor(chosen, "/default"));
        Assert.Equal("/default", LinkCache.RootFor(string.Empty, "/default"));
        Assert.Equal("/default", LinkCache.RootFor("relative/path", "/default"));
    }

    /// <summary>
    /// A user mapping's own stream limit and speed apply to that user, and everyone else gets the
    /// server entry's. The lower of the two speeds applies.
    /// True: one user can be held tighter, or looser, than the rest on that server.
    /// False: a per user setting would be ignored, or would raise a user above the server's speed.
    /// </summary>
    [Fact]
    public void UserCaps_OverrideTheServersDefaults()
    {
        var capped = Guid.NewGuid();
        var other = Guid.NewGuid();
        var server = new SourceServer
        {
            LinkMaxStreamsPerUser = 2,
            LinkStreamSpeedMbps = 80,
            UserMappings = new List<UserMapping> { new() { LocalUserId = capped.ToString(), SourceUserId = Guid.NewGuid().ToString(), LinkMaxStreams = 1, LinkStreamSpeedMbps = 8 } }
        };

        Assert.Equal(1, server.GetLinkMaxStreamsFor(capped));
        Assert.Equal(2, server.GetLinkMaxStreamsFor(other));
        Assert.Equal(1_000_000, server.GetLinkStreamSpeedBytes(false, capped));
        Assert.Equal(10_000_000, server.GetLinkStreamSpeedBytes(false, other));
        Assert.Equal(10_000_000, server.GetLinkStreamSpeedBytes(false, null));

        server.UserMappings[0].LinkStreamSpeedMbps = 800;
        Assert.Equal(10_000_000, server.GetLinkStreamSpeedBytes(false, capped));
        server.LinkStreamSpeedMbps = 0;
        Assert.Equal(100_000_000, server.GetLinkStreamSpeedBytes(false, capped));
    }

    private LinkCache Cache() => new(new FakeConfig(_config), _dir, NullLogger<LinkCache>.Instance, () => _now);

    private static void Put(LinkCache cache, string linkId, int size)
    {
        File.WriteAllBytes(cache.PartPathFor(linkId, ".mkv"), new byte[size]);
        Assert.True(cache.Complete(linkId, ".mkv", size, CacheReason.Played));
    }

    // A file of a real size on disk without writing it out: the length is set, and nothing is stored.
    private static void PutSized(LinkCache cache, string linkId, long size)
    {
        using (var file = new FileStream(cache.PartPathFor(linkId, ".mkv"), FileMode.Create, FileAccess.Write))
        {
            file.SetLength(size);
        }

        Assert.True(cache.Complete(linkId, ".mkv", size, CacheReason.Played));
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
