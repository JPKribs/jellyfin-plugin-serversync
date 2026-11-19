using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Tasks.MetadataSync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Tasks.MetadataSync;

/// <summary>
/// The metadata scan runs only when a category it can apply is on, asks a source for nothing this server cannot match, and keys an item by the local file it describes.
/// </summary>
public sealed class RefreshMetadataSyncTableTaskTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly MetadataSyncTableManager _manager;
    private readonly FakeConfigManager _config = new();
    private readonly TaskClientFactory _factory = new();
    private readonly List<ScanSource> _sources = new();
    private readonly SourceServer _server;
    private readonly MetadataSyncTableService _service;

    public RefreshMetadataSyncTableTaskTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-metadata-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        _manager = new MetadataSyncTableManager(new FixedProvider(_database), NullLogger<MetadataSyncTableManager>.Instance);
        _service = new MetadataSyncTableService(
            NullLogger<MetadataSyncTableService>.Instance,
            InterfaceStub.Create<ILibraryManager>().Proxy,
            InterfaceStub.Create<ILocalItems>().Proxy,
            new NoLinks());
        _server = new SourceServer
        {
            Key = "server-a",
            Name = "Source A",
            Url = "http://server-a:8096",
            ApiKey = "k",
            LibraryMappings =
            {
                new LibraryMapping
                {
                    SourceLibraryId = Guid.NewGuid().ToString("N"),
                    SourceRootPath = "/src/movies",
                    LocalLibraryId = Guid.NewGuid().ToString("N"),
                    LocalRootPath = "/local/movies"
                }
            }
        };
        _config.Configuration.Servers.Add(_server);
        _config.Configuration.EnableMetadataSync = true;
    }

    public void Dispose()
    {
        foreach (var source in _sources)
        {
            source.Dispose();
        }

        _database.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class Probe : RefreshMetadataSyncTableTask
    {
        public Probe(RefreshMetadataSyncTableTaskTests test, MediaSegmentStore? segments = null)
            : base(NullLogger<RefreshMetadataSyncTableTask>.Instance, test._config, test._factory, test._service, test._manager, null!, null!, null, segments, QueueSamples.Host("this-server"))
        {
        }

        public Task Finalize(IReadOnlyList<ScanSource> sources)
        {
            Sources = sources;
            return FinalizeAsync(CancellationToken.None);
        }

        public string MutexKey => ModuleMutexKey;

        public string Scope => ScopeSettings;

        public int Parallelism => BuildRecordParallelism;

        public bool Incremental => SupportsIncremental;

        public bool Enabled() => IsEnabled();

        public Task<IList<MetadataWork>> List(ScanSource source) => GetListAsync(source, new TaskProgress(), CancellationToken.None);

        public string? KeyOf(MetadataWork work) => PriorityKeyOf(work);

        public string? KeyOf(MetadataSyncItem record) => PriorityKeyOf(record);

        public (string SourceLibraryId, string SourceItemId) RowKey(MetadataSyncItem record) => ExtractKey(record);

        public Task PrepareContest() => PrepareContestAsync(CancellationToken.None);

        public bool Outranks(MetadataSyncItem record) => MayOutrankOwner(record);

        public void Completed(PluginConfiguration config, DateTime at) => RecordRunCompleted(config, at);
    }

    /// <summary>
    /// Finishing a scan forgets the segments kept for files the Metadata table no longer holds, only while Enable Media Segment Provider is on.
    /// True: the local segment table never outgrows the files being synced, and a server that left it off keeps what it has.
    /// False: removed files would keep segments forever, or turning the switch off would clear them.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FinalizeAsync_SegmentProviderSwitch_DecidesWhetherUntrackedSegmentsAreForgotten(bool on)
    {
        _config.Configuration.MetadataSyncSegmentProvider = on;
        _manager.Upsert(new MetadataSyncItem { SourceLibraryId = "lib", LocalLibraryId = "local", SourceItemId = "film-on-a", LocalItemId = "local-film", ItemType = "Movie", ServerKey = _server.Key, StatusDate = DateTime.UtcNow });
        var store = new MediaSegmentStore(new FixedProvider(_database), NullLogger<MediaSegmentStore>.Instance);
        store.Save(new[]
        {
            new SourceMediaSegments("local-film", "a", "film-on-a", Array.Empty<Jellyfin.Plugin.ServerSync.Models.Peer.PeerSegment>(), null, null, DateTime.UtcNow),
            new SourceMediaSegments("local-gone", "a", "gone", Array.Empty<Jellyfin.Plugin.ServerSync.Models.Peer.PeerSegment>(), null, null, DateTime.UtcNow)
        });

        await new Probe(this, store).Finalize(Array.Empty<ScanSource>());

        Assert.NotNull(store.Get("local-film"));
        Assert.Equal(on, store.Get("local-gone") is null);
    }

    private ScanSource Source()
    {
        var source = new ScanSource(_server, _factory.ClientFor(_server), 0);
        _sources.Add(source);
        return source;
    }

    private void SetCategories(bool metadata, bool images, bool people, bool studios, bool genres = false, bool tags = false)
    {
        var config = _config.Configuration;
        config.MetadataSyncMetadata = metadata;
        config.MetadataSyncImages = images;
        config.MetadataSyncPeople = people;
        config.MetadataSyncStudios = studios;
        config.MetadataSyncGenres = genres;
        config.MetadataSyncTags = tags;
    }

    /// <summary>
    /// The scan runs when Metadata is on, a source and a library mapping exist, and at least one of metadata, images, people, studios, or media segments is on.
    /// True: any one applicable category is enough to scan.
    /// False: the scan stays off although a category is on, or runs with nothing it could apply.
    /// </summary>
    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, false, false, true)]
    public void IsEnabled_AnyOneApplicableCategory_IsEnough(bool metadata, bool images, bool people, bool studios, bool segments = false)
    {
        SetCategories(metadata, images, people, studios);
        _config.Configuration.MetadataSyncSegmentProvider = segments;

        Assert.True(new Probe(this).Enabled());
    }

    /// <summary>
    /// The scan is off when Metadata is off, no source or library mapping exists, or only genres and tags are on.
    /// True: genres and tags ride on the metadata category and cannot run a scan by themselves.
    /// False: a full scan of every library runs to apply nothing.
    /// </summary>
    [Theory]
    [InlineData("sync")]
    [InlineData("server")]
    [InlineData("mapping")]
    [InlineData("categories")]
    public void IsEnabled_NothingToScanOrApply_IsOff(string off)
    {
        SetCategories(metadata: off != "categories", images: false, people: false, studios: false, genres: true, tags: true);
        _config.Configuration.EnableMetadataSync = off != "sync";
        _server.IsEnabled = off != "server";
        _server.LibraryMappings[0].IsEnabled = off != "mapping";

        Assert.False(new Probe(this).Enabled());
    }

    /// <summary>
    /// A mapping whose local library is unset, malformed, or missing gives no work and asks the source for nothing.
    /// True: full metadata is never fetched for a library this server does not hold.
    /// False: a whole source library is paged although nothing here could match it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("55555555555555555555555555555555")]
    public async Task GetListAsync_LocalLibraryUnusable_ReturnsNothingWithoutAsking(string localLibraryId)
    {
        _server.LibraryMappings[0].LocalLibraryId = localLibraryId;

        var work = await new Probe(this).List(Source());

        Assert.Empty(work);
        Assert.Empty(_factory.HandlerFor("server-a").Requests);
    }

    /// <summary>
    /// An offer is keyed by the local path its source path translates to, and a row by its local path.
    /// True: two servers describing the same local file collide, and the offer meets its stored row.
    /// False: both servers' metadata is applied to one file in turn.
    /// </summary>
    [Fact]
    public void PriorityKeyOf_IsTheLocalPath()
    {
        var task = new Probe(this);
        var work = new MetadataWork(Source(), _server.LibraryMappings[0], new BaseItemDto { Id = Guid.NewGuid(), Path = "/src/movies/Film/Film.mkv" }, false);
        var row = new MetadataSyncItem { LocalPath = "/local/movies/Film/Film.mkv" };

        Assert.Equal("/local/movies/Film/Film.mkv", task.KeyOf(work)!.Replace('\\', '/'));
        Assert.Equal(task.KeyOf(work), task.KeyOf(row));
    }

    /// <summary>
    /// An offer with no path, and a row with no local path, have no collision key.
    /// True: an item that cannot be placed never collides with anything.
    /// False: every pathless item shares one key and all but one are dropped.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void PriorityKeyOf_NoPath_IsNull(string? path)
    {
        var task = new Probe(this);
        var work = new MetadataWork(Source(), _server.LibraryMappings[0], new BaseItemDto { Id = Guid.NewGuid(), Path = path }, false);

        Assert.Null(task.KeyOf(work));
        Assert.Null(task.KeyOf(new MetadataSyncItem { LocalPath = path }));
    }

    /// <summary>
    /// A row's key is its source library and source item.
    /// True: one source item is one row.
    /// False: the same item id in two libraries overwrites one row.
    /// </summary>
    [Fact]
    public void ExtractKey_IsTheSourceLibraryAndItem()
    {
        var key = new Probe(this).RowKey(new MetadataSyncItem { SourceLibraryId = "lib", SourceItemId = "item", LocalItemId = "x" });

        Assert.Equal(("lib", "item"), key);
    }

    /// <summary>
    /// Turning any category or folder items on or off changes the scan's scope.
    /// True: the next scan reads everything again after a category changes.
    /// False: a changes only scan trusts rows built under the old categories and the new one never fills in.
    /// </summary>
    [Theory]
    [InlineData(nameof(PluginConfiguration.MetadataSyncMetadata))]
    [InlineData(nameof(PluginConfiguration.MetadataSyncGenres))]
    [InlineData(nameof(PluginConfiguration.MetadataSyncTags))]
    [InlineData(nameof(PluginConfiguration.MetadataSyncStudios))]
    [InlineData(nameof(PluginConfiguration.MetadataSyncPeople))]
    [InlineData(nameof(PluginConfiguration.MetadataSyncImages))]
    [InlineData(nameof(PluginConfiguration.MetadataSyncFolderItems))]
    public void ScopeSettings_ChangesWithEachCategory(string setting)
    {
        var task = new Probe(this);
        var before = task.Scope;
        var property = typeof(PluginConfiguration).GetProperty(setting)!;

        property.SetValue(_config.Configuration, !(bool)property.GetValue(_config.Configuration)!);

        Assert.NotEqual(before, task.Scope);
    }

    /// <summary>
    /// The number of rows built at once follows the setting, held between 1 and 16.
    /// True: an administrator can lower the scan's CPU use, and a bad value cannot stall or flood it.
    /// False: a setting of zero stops the build, or a huge one starves playback.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(4, 4)]
    [InlineData(int.MaxValue, 16)]
    public void BuildRecordParallelism_IsTheSettingHeldInRange(int setting, int expected)
    {
        _config.Configuration.RefreshParallelism = setting;

        Assert.Equal(expected, new Probe(this).Parallelism);
    }

    /// <summary>
    /// A scan reads an item's cast when it is new, the source saved it since the last scan, its stored cast is missing, or what the source saved is not known.
    /// True: a full scan skips the cast only for items the source has not touched, which is most of a library.
    /// False: every full scan rebuilds every cast on the source, or a new or edited item keeps a stale cast.
    /// </summary>
    [Theory]
    [InlineData("unknown", true)]
    [InlineData("new", true)]
    [InlineData("saved", true)]
    [InlineData("no cast stored", true)]
    [InlineData("untouched", false)]
    public void CastNeeded_ReadsOnlyWhereTheCastMayHaveChanged(string state, bool expected)
    {
        var id = Guid.NewGuid();
        var prior = new MetadataSyncItem { SourceItemId = id.ToString("N") };
        prior.People.UpdateSource("[]");
        if (state == "no cast stored")
        {
            prior.People.Source = null;
        }

        var saved = state switch
        {
            "unknown" => null,
            "saved" => new HashSet<Guid> { id },
            _ => new HashSet<Guid>()
        };

        Assert.Equal(expected, RefreshMetadataSyncTableTask.CastNeeded(saved, id, state == "new" ? null : prior));
    }

    /// <summary>
    /// A change to the metadata, images, or studios the source offers counts as a change besides the cast, and a change to the cast alone does not.
    /// True: an item whose other details changed without a save the listing caught still has its cast read.
    /// False: a cast that changed with other details is missed, or every item has its cast read one at a time.
    /// </summary>
    [Theory]
    [InlineData("metadata", true)]
    [InlineData("images", true)]
    [InlineData("studios", true)]
    [InlineData("people", false)]
    [InlineData("none", false)]
    public void SourceChangedBesidesCast_IgnoresTheCastOnly(string changed, bool expected)
    {
        static MetadataSyncItem Row(string metadata, string images, string studios, string people)
        {
            var row = new MetadataSyncItem();
            row.Metadata.UpdateSource(metadata);
            row.Images.UpdateSource(images);
            row.Studios.UpdateSource(studios);
            row.People.UpdateSource(people);
            return row;
        }

        var prior = Row("{\"Overview\":\"a\"}", "{}", "[\"Pixar\"]", "[]");
        var fresh = Row(
            changed == "metadata" ? "{\"Overview\":\"b\"}" : "{\"Overview\":\"a\"}",
            changed == "images" ? "{\"Primary\":[{\"ImageType\":\"Primary\",\"ImageIndex\":0,\"Size\":9,\"Tag\":\"t\"}]}" : "{}",
            changed == "studios" ? "[\"Disney\"]" : "[\"Pixar\"]",
            changed == "people" ? "[{\"Name\":\"Alice\"}]" : "[]");

        Assert.Equal(expected, RefreshMetadataSyncTableTask.SourceChangedBesidesCast(fresh, prior));
    }

    /// <summary>
    /// Under Ranked precedence no server is asked for stamps, and no row may outrank its owner.
    /// True: Ranked follows the list order alone and costs no peer request.
    /// False: a lower ranked server overrides the owner although the administrator chose rank.
    /// </summary>
    [Fact]
    public async Task PrepareContestAsync_RankedPrecedence_LetsNoRowOutrankItsOwner()
    {
        _config.Configuration.MetadataSyncPrecedence = SyncPrecedence.Rank;
        var task = new Probe(this);

        await task.PrepareContest();

        Assert.False(task.Outranks(new MetadataSyncItem { ServerKey = "server-a" }));
        Assert.False(task.Outranks(new MetadataSyncItem()));
    }

    /// <summary>
    /// The scan may list only what changed, shares the Metadata lock, and stamps the Metadata time when it finishes.
    /// True: most scans are cheap, the scan and the apply never overlap, and the dashboard shows the run.
    /// False: every scan reads every item, or a scan overwrites a row mid apply.
    /// </summary>
    [Fact]
    public void RecordRunCompleted_StampsTheMetadataTimeUnderTheMetadataLock()
    {
        var task = new Probe(this);
        var config = new PluginConfiguration();
        var at = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        task.Completed(config, at);

        Assert.Equal(at, config.LastMetadataSyncTime);
        Assert.Null(config.LastPeopleSyncTime);
        Assert.Equal("Metadata", task.MutexKey);
        Assert.True(task.Incremental);
        Assert.Equal("ServerSyncRefreshMetadataTable", task.Key);
    }
}
