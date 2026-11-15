using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.PeopleSync;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Tasks.PeopleSync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using SourceItemDto = Jellyfin.Sdk.Generated.Models.BaseItemDto;

namespace Jellyfin.Plugin.ServerSync.Tests.Tasks.PeopleSync;

/// <summary>
/// The people scan tracks only persons that exist on both servers, matched by name, and never treats an empty or failed answer from either side as people having been removed.
/// </summary>
public sealed class RefreshPeopleSyncTableTaskTests : IDisposable
{
    private const string ServerKey = "server-a";
    private static readonly Guid SourcePersonId = Guid.Parse("aaaaaaaa-1111-0000-0000-000000000001");

    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly PeopleSyncTableManager _manager;
    private readonly FakeConfigManager _config = new();
    private readonly TaskClientFactory _factory = new();
    private readonly List<ScanSource> _sources = new();
    private readonly List<BaseItem> _localPersons = new();
    private readonly SourceServer _server;
    private readonly InterfaceStub _libraryStub;
    private readonly ILibraryManager _library;

    public RefreshPeopleSyncTableTaskTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-people-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        _manager = new PeopleSyncTableManager(new FixedProvider(_database), NullLogger<PeopleSyncTableManager>.Instance);
        _server = new SourceServer { Key = ServerKey, Name = "Source A", Url = "http://server-a:8096", ApiKey = "k" };
        _config.Configuration.Servers.Add(_server);
        _config.Configuration.EnablePeopleSync = true;
        _config.Configuration.PeopleSyncImages = false;
        _config.Configuration.PeopleSyncPrecedence = SyncPrecedence.Rank;
        (_library, _libraryStub) = InterfaceStub.Create<ILibraryManager>();
        _libraryStub.Answer(nameof(ILibraryManager.GetItemList), args =>
        {
            var name = ((InternalItemsQuery)args[0]!).Name;
            return name is null ? _localPersons : _localPersons.Where(p => p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
        });
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

    private sealed class Probe : RefreshPeopleSyncTableTask
    {
        public Probe(RefreshPeopleSyncTableTaskTests test)
            : base(NullLogger<RefreshPeopleSyncTableTask>.Instance, test._library, test._factory, test._config, test._manager, null!)
        {
        }

        public string MutexKey => ModuleMutexKey;

        public int Parallelism => BuildRecordParallelism;

        public bool Enabled() => IsEnabled();

        public void StartRun() => OnRunStarting();

        public Task<IList<PersonWork>> List(ScanSource source) => GetListAsync(source, new TaskProgress(), CancellationToken.None);

        public Task<PeopleSyncItem?> Build(PersonWork work, IReadOnlyDictionary<string, PeopleSyncItem>? existing = null)
            => BuildRecordAsync(work, existing ?? new Dictionary<string, PeopleSyncItem>(), CancellationToken.None);

        public void PrepareOne(PersonWork work) => PrepareForOne(work);

        public string? KeyOf(PersonWork? work) => PriorityKeyOf(work!);

        public string? KeyOf(PeopleSyncItem? record) => PriorityKeyOf(record!);

        public string RowKey(PeopleSyncItem record) => ExtractKey(record);

        public Task PrepareContest() => PrepareContestAsync(CancellationToken.None);

        public bool Outranks(PeopleSyncItem record) => MayOutrankOwner(record);

        public void Completed(PluginConfiguration config, DateTime at) => RecordRunCompleted(config, at);

        protected override Task ResolveConflictsAsync(IList<PeopleSyncItem> queued, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private ScanSource Source()
    {
        var source = new ScanSource(_server, _factory.ClientFor(_server), 0);
        _sources.Add(source);
        return source;
    }

    private Person LocalPerson(string name, string? overview = null)
    {
        var person = new Person { Id = Guid.NewGuid(), Name = name, Overview = overview };
        _localPersons.Add(person);
        return person;
    }

    private void SourceHolds(params string[] names)
    {
        var items = names.Select((name, i) => "{\"Id\":\"aaaaaaaa-1111-0000-0000-00000000000" + (i + 1) + "\",\"Name\":" + (name.Length == 0 ? "null" : "\"" + name + "\"") + ",\"Type\":\"Person\",\"Overview\":\"from source\"}");
        _factory.HandlerFor(ServerKey).OnJson("/Persons", "{\"Items\":[" + string.Join(",", items) + "],\"TotalRecordCount\":" + names.Length + "}");
    }

    private int LibraryReads => _libraryStub.Calls.Count(c => c.Name == nameof(ILibraryManager.GetItemList));

    private PersonWork Work(string? name) => new(Source(), new SourceItemDto { Id = SourcePersonId, Name = name, Overview = "from source" });

    private void Track(string name)
        => _manager.Upsert(new PeopleSyncItem { PersonName = name, LocalPersonId = Guid.NewGuid().ToString("N"), ServerKey = ServerKey, Status = SyncStatus.Synced });

    /// <summary>
    /// The scan runs only with People on and a source configured.
    /// True: turning People off stops the scan.
    /// False: the scan fetches every person from every source for a sync type that is off.
    /// </summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void IsEnabled_NeedsPeopleAndASource(bool peopleOn, bool hasServer, bool expected)
    {
        _config.Configuration.EnablePeopleSync = peopleOn;
        _server.IsEnabled = hasServer;

        Assert.Equal(expected, new Probe(this).Enabled());
    }

    /// <summary>
    /// Source persons are matched to local ones by name without regard to case, once each, and the rest are dropped.
    /// True: only persons this server already has get a row, one row for each.
    /// False: unmatched persons fill the table, or one person is built twice.
    /// </summary>
    [Fact]
    public async Task GetListAsync_SourcePersons_KeepsEachLocalMatchOnce()
    {
        LocalPerson("Alice Smith");
        LocalPerson("Carol Jones");
        SourceHolds("alice smith", "Bob Brown", "Alice Smith", string.Empty);

        var work = await new Probe(this).List(Source());

        var only = Assert.Single(work);
        Assert.Equal("alice smith", only.Person.Name);
        Assert.Equal(ServerKey, only.Source.Key);
    }

    /// <summary>
    /// With no persons on this server, the source is not asked and nothing is listed.
    /// True: a server with no people costs the source no request.
    /// False: the whole person catalog is downloaded although nothing could match.
    /// </summary>
    [Fact]
    public async Task GetListAsync_NoLocalPersons_ReturnsNothingWithoutAsking()
    {
        SourceHolds("Alice Smith");

        var work = await new Probe(this).List(Source());

        Assert.Empty(work);
        Assert.Equal(0, _factory.HandlerFor(ServerKey).CountOf("/Persons"));
    }

    /// <summary>
    /// A source that answers the person list with an error, or with no persons at all, gives no work and no exception.
    /// True: a failing source is tolerated for this run.
    /// False: the scan throws and takes Sync Information down with it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetListAsync_SourceErrorsOrIsEmpty_ReturnsNothing(bool error)
    {
        LocalPerson("Alice Smith");
        if (error)
        {
            _factory.HandlerFor(ServerKey).On("/Persons", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
        else
        {
            SourceHolds();
        }

        var work = await new Probe(this).List(Source());

        Assert.Empty(work);
    }

    /// <summary>
    /// The local persons are read once for a run and again after the next run starts.
    /// True: a second server reuses the first one's list, and a person added between runs is seen.
    /// False: Jellyfin's library is queried for every server, or a new person is never noticed.
    /// </summary>
    [Fact]
    public async Task GetListAsync_LocalPersons_AreReadOnceForEachRun()
    {
        LocalPerson("Alice Smith");
        SourceHolds("Alice Smith");
        var task = new Probe(this);

        await task.List(Source());
        await task.List(Source());
        var readsInOneRun = LibraryReads;
        task.StartRun();
        await task.List(Source());

        Assert.Equal(1, readsInOneRun);
        Assert.Equal(2, LibraryReads);
    }

    /// <summary>
    /// A person held on both servers gets a row carrying both ids, the server key, and both sides' metadata.
    /// True: the apply knows which local person to write and which server to read from.
    /// False: the row cannot be applied, or is applied against another server.
    /// </summary>
    [Fact]
    public async Task BuildRecordAsync_MatchedPerson_BuildsTheRowFromBothSides()
    {
        var local = LocalPerson("Alice Smith", overview: "local text");
        SourceHolds("Alice Smith");
        var task = new Probe(this);
        var work = Assert.Single(await task.List(Source()));

        var row = await task.Build(work);

        Assert.NotNull(row);
        Assert.Equal("Alice Smith", row!.PersonName);
        Assert.Equal(local.Id.ToString("N"), row.LocalPersonId);
        Assert.Equal("aaaaaaaa111100000000000000000001", row.SourcePersonId);
        Assert.Equal(ServerKey, row.ServerKey);
        Assert.Contains("from source", row.Metadata.Source, StringComparison.Ordinal);
        Assert.Contains("local text", row.Metadata.Local, StringComparison.Ordinal);
        Assert.NotNull(row.Metadata.SourceHash);
        Assert.True(row.HasMetadataChanges);
    }

    /// <summary>
    /// A stored row is reused, and with image sync off its stored image values are cleared.
    /// True: the row keeps its id and status, and turning images off stops image changes showing.
    /// False: every scan makes a new row, or old image values keep the row queued.
    /// </summary>
    [Fact]
    public async Task BuildRecordAsync_StoredRowAndImagesOff_ReusesItAndClearsImages()
    {
        LocalPerson("Alice Smith");
        SourceHolds("Alice Smith");
        var task = new Probe(this);
        var work = Assert.Single(await task.List(Source()));
        var stored = new PeopleSyncItem { PersonName = "Alice Smith", Id = 7, Status = SyncStatus.Ignored };
        stored.Images.UpdateSource("{\"Primary\":[]}");
        stored.Images.Local = "{}";

        var row = await task.Build(work, new Dictionary<string, PeopleSyncItem>(StringComparer.OrdinalIgnoreCase) { ["Alice Smith"] = stored });

        Assert.Same(stored, row);
        Assert.Equal(SyncStatus.Ignored, row!.Status);
        Assert.Null(row.Images.Source);
        Assert.Null(row.Images.Local);
        Assert.Null(row.Images.SourceHash);
        Assert.False(row.HasImagesChanges);
    }

    /// <summary>
    /// A person with no name, or with no local match, gets no row.
    /// True: a person this server does not have is never tracked.
    /// False: rows are built that can never be applied.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Nobody Here")]
    public async Task BuildRecordAsync_UnnamedOrUnmatchedPerson_ReturnsNull(string? name)
    {
        LocalPerson("Alice Smith");
        SourceHolds("Alice Smith");
        var task = new Probe(this);
        await task.List(Source());

        var row = await task.Build(Work(name));

        Assert.Null(row);
    }

    /// <summary>
    /// For a single person from a hint, only an exact name match on this server is used.
    /// True: a hint for one person builds that person's row without listing every person.
    /// False: a near match is taken, and one person's details are written onto another.
    /// </summary>
    [Theory]
    [InlineData("Alice Smith", "alice smith", true)]
    [InlineData("Alice Smithson", "Alice Smith", false)]
    [InlineData("Carol Jones", "Alice Smith", false)]
    public async Task PrepareForOne_SinglePerson_UsesOnlyAnExactNameMatch(string localName, string hintedName, bool built)
    {
        LocalPerson(localName);
        var task = new Probe(this);
        var work = Work(hintedName);

        task.PrepareOne(work);
        var row = await task.Build(work);

        Assert.Equal(built, row is not null);
    }

    /// <summary>
    /// A hint for a person with no name reads nothing from the library and builds nothing.
    /// True: a malformed hint is a quiet no-op.
    /// False: the library is queried with no name and returns an arbitrary person.
    /// </summary>
    [Fact]
    public async Task PrepareForOne_UnnamedPerson_ReadsNothing()
    {
        LocalPerson("Alice Smith");
        var task = new Probe(this);
        var work = Work(null);

        task.PrepareOne(work);
        var row = await task.Build(work);

        Assert.Null(row);
        Assert.Equal(0, LibraryReads);
    }

    /// <summary>
    /// A source that lists its persons cleanly lets a row for a person it no longer has be pruned.
    /// True: a person removed from the source stops being tracked.
    /// False: the table keeps rows for people that are gone forever.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SourceListsCleanly_PrunesAPersonItNoLongerHas()
    {
        LocalPerson("Alice Smith");
        SourceHolds("Alice Smith");
        Track("Gone Person");

        await new Probe(this).ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.Null(_manager.GetByKey("Gone Person"));
        Assert.NotNull(_manager.GetByKey("Alice Smith"));
        Assert.NotNull(_config.Configuration.LastPeopleSyncTime);
    }

    /// <summary>
    /// A local catalog that is empty while rows exist, a source that errors, and a source that lists no persons each stop the prune.
    /// True: a Jellyfin rescan or a source outage never deletes the tracking rows.
    /// False: every row, with its Ignored choices, is wiped whenever one side answers with nothing.
    /// </summary>
    [Theory]
    [InlineData("local-empty")]
    [InlineData("source-error")]
    [InlineData("source-empty")]
    public async Task ExecuteAsync_UnreliableAnswer_PrunesNothing(string shape)
    {
        Track("Gone Person");
        switch (shape)
        {
            case "local-empty":
                SourceHolds("Alice Smith");
                break;
            case "source-error":
                LocalPerson("Alice Smith");
                _factory.HandlerFor(ServerKey).On("/Persons", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
                break;
            default:
                LocalPerson("Alice Smith");
                SourceHolds();
                break;
        }

        await new Probe(this).ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.NotNull(_manager.GetByKey("Gone Person"));
    }

    /// <summary>
    /// Two servers collide on the person's name, which is also the row's key.
    /// True: one person has one row, supplied by the highest ranked server that has them.
    /// False: two servers each write the same person in turn.
    /// </summary>
    [Fact]
    public void PriorityKeyOf_IsThePersonName()
    {
        var task = new Probe(this);
        var row = new PeopleSyncItem { PersonName = "Alice Smith" };

        Assert.Equal("Alice Smith", task.KeyOf(Work("Alice Smith")));
        Assert.Equal("Alice Smith", task.KeyOf(row));
        Assert.Equal("Alice Smith", task.RowKey(row));
        Assert.Null(task.KeyOf((PersonWork?)null));
        Assert.Null(task.KeyOf((PeopleSyncItem?)null));
    }

    /// <summary>
    /// The number of rows built at once follows the setting, held between 1 and 16.
    /// True: an administrator can lower the scan's CPU use, and a bad value cannot stall or flood it.
    /// False: a setting of zero stops the build, or a huge one starves playback.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(4, 4)]
    [InlineData(16, 16)]
    [InlineData(int.MaxValue, 16)]
    public void BuildRecordParallelism_IsTheSettingHeldInRange(int setting, int expected)
    {
        _config.Configuration.RefreshParallelism = setting;

        Assert.Equal(expected, new Probe(this).Parallelism);
    }

    /// <summary>
    /// Under Ranked precedence no server is asked for stamps, and no row may outrank its owner.
    /// True: Ranked follows the list order alone and costs no peer request.
    /// False: a lower ranked server overrides the owner although the administrator chose rank.
    /// </summary>
    [Fact]
    public async Task PrepareContestAsync_RankedPrecedence_LetsNoRowOutrankItsOwner()
    {
        var task = new Probe(this);

        await task.PrepareContest();

        Assert.False(task.Outranks(new PeopleSyncItem { PersonName = "Alice Smith", ServerKey = ServerKey }));
        Assert.False(task.Outranks(new PeopleSyncItem { PersonName = "Alice Smith" }));
    }

    /// <summary>
    /// A finished scan stamps the People time, under the lock the People apply shares.
    /// True: the dashboard shows the scan, and the scan and the apply never overlap.
    /// False: another sync type's time moves, or the two tasks write the table together.
    /// </summary>
    [Fact]
    public void RecordRunCompleted_StampsThePeopleTimeUnderThePeopleLock()
    {
        var task = new Probe(this);
        var config = new PluginConfiguration();
        var at = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        task.Completed(config, at);

        Assert.Equal(at, config.LastPeopleSyncTime);
        Assert.Null(config.LastMetadataSyncTime);
        Assert.Equal("People", task.MutexKey);
        Assert.Equal("ServerSyncRefreshPeopleTable", task.Key);
    }
}
