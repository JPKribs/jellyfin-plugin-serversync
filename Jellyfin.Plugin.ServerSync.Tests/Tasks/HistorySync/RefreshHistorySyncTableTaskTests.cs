using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Tasks.HistorySync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Tasks.HistorySync;

/// <summary>
/// The history scan runs only when users and libraries are mapped, asks a source nothing it cannot match here, and lets the server whose history moved speak for a file two servers share.
/// </summary>
public sealed class RefreshHistorySyncTableTaskTests : IDisposable
{
    private static readonly string LocalUser = Guid.Parse("22222222-2222-2222-2222-222222222222").ToString("N");
    private static readonly string LocalLibrary = Guid.Parse("55555555-5555-5555-5555-555555555555").ToString("N");
    private static readonly DateTime Played = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly HistorySyncTableManager _manager;
    private readonly FakeConfigManager _config = new();
    private readonly TaskClientFactory _factory = new();
    private readonly List<ScanSource> _sources = new();
    private readonly InterfaceStub _libraryStub;
    private readonly ILibraryManager _library;

    public RefreshHistorySyncTableTaskTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-history-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        _manager = new HistorySyncTableManager(new FixedProvider(_database), NullLogger<HistorySyncTableManager>.Instance);
        (_library, _libraryStub) = InterfaceStub.Create<ILibraryManager>();
        _config.Configuration.EnableHistorySync = true;
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

    private sealed class Probe : RefreshHistorySyncTableTask
    {
        public Probe(RefreshHistorySyncTableTaskTests test)
            : base(NullLogger<RefreshHistorySyncTableTask>.Instance, test._config, test._factory, null!, test._library, new NoLinks(), test._manager)
        {
        }

        /// <summary>Runs in place of the listing, while the run holds the stored rows.</summary>
        public Func<Task>? DuringRun { get; set; }

        public bool Enabled() => IsEnabled();

        public string MutexKey => ModuleMutexKey;

        public string Scope => ScopeSettings;

        public Task<IList<HistoryWork>> List(ScanSource source) => base.GetListAsync(source, new TaskProgress(), CancellationToken.None);

        public string? KeyOf(HistoryWork work) => PriorityKeyOf(work);

        public string? KeyOf(HistorySyncItem record) => PriorityKeyOf(record);

        public Task<IReadOnlyDictionary<string, HistoryWork>> Contest(params HistoryWork[] offers)
            => ContestAsync(new Dictionary<string, IReadOnlyList<HistoryWork>> { ["file"] = offers }, CancellationToken.None);

        public bool Outranks(HistorySyncItem record) => MayOutrankOwner(record);

        public bool Retires(HistorySyncItem record) => CanRetire(record);

        public (string SourceUserId, string SourceItemId) RowKey(HistorySyncItem record) => ExtractKey(record);

        public void Completed(PluginConfiguration config, DateTime at) => RecordRunCompleted(config, at);

        protected override async Task<IList<HistoryWork>> GetListAsync(ScanSource source, IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (DuringRun is null)
            {
                return await base.GetListAsync(source, progress, cancellationToken).ConfigureAwait(false);
            }

            await DuringRun().ConfigureAwait(false);
            DuringRun = null;
            return Array.Empty<HistoryWork>();
        }
    }

    private SourceServer AddServer(string key, string sourceUser)
    {
        var server = new SourceServer
        {
            Key = key,
            Name = key,
            Url = "http://" + key + ":8096",
            ApiKey = "k",
            LibraryMappings =
            {
                new LibraryMapping
                {
                    SourceLibraryId = Guid.NewGuid().ToString("N"),
                    SourceLibraryName = "Films",
                    SourceRootPath = "/src/" + key,
                    LocalLibraryId = LocalLibrary,
                    LocalRootPath = "/local/movies"
                }
            },
            UserMappings = { new UserMapping { SourceUserId = sourceUser, LocalUserId = LocalUser } }
        };
        _config.Configuration.Servers.Add(server);
        return server;
    }

    private ScanSource Source(SourceServer server, int priority)
    {
        var source = new ScanSource(server, _factory.ClientFor(server), priority);
        _sources.Add(source);
        return source;
    }

    private HistoryWork Offer(SourceServer server, int priority, Guid itemId, bool played = true)
        => new(Source(server, priority), server.UserMappings[0], server.LibraryMappings[0], new BaseItemDto
        {
            Id = itemId,
            Path = "/src/" + server.Key + "/Film/Film.mkv",
            UserData = new UserItemDataDto
            {
                Played = played,
                PlayCount = 1,
                PlaybackPositionTicks = 0,
                IsFavorite = false,
                LastPlayedDate = new DateTimeOffset(Played)
            }
        });

    private void Seen(SourceServer server, Guid itemId)
        => _manager.Upsert(new HistorySyncItem
        {
            SourceUserId = server.UserMappings[0].SourceUserId,
            LocalUserId = LocalUser,
            SourceLibraryId = server.LibraryMappings[0].SourceLibraryId,
            SourceItemId = itemId.ToString("N"),
            ServerKey = server.Key,
            Status = SyncStatus.Synced,
            SourceIsPlayed = true,
            SourcePlayCount = 1,
            SourcePlaybackPositionTicks = 0,
            SourceIsFavorite = false,
            SourceLastPlayedDate = Played
        });

    private async Task<IReadOnlyDictionary<string, HistoryWork>> ContestDuringRunAsync(params HistoryWork[] offers)
    {
        var task = new Probe(this);
        IReadOnlyDictionary<string, HistoryWork>? winners = null;
        task.DuringRun = async () => winners = await task.Contest(offers);

        await task.ExecuteAsync(new TaskProgress(), CancellationToken.None);

        return winners!;
    }

    /// <summary>
    /// The scan runs only with History on, a source, an enabled user mapping, and an enabled library mapping.
    /// True: the scan stays idle until there is something it could match.
    /// False: a scan with nothing mapped connects to every source and prunes the table.
    /// </summary>
    [Theory]
    [InlineData("none", true)]
    [InlineData("history", false)]
    [InlineData("server", false)]
    [InlineData("user", false)]
    [InlineData("library", false)]
    public void IsEnabled_NeedsHistoryASourceAndBothMappings(string off, bool expected)
    {
        var server = AddServer("a", Guid.NewGuid().ToString("N"));
        _config.Configuration.EnableHistorySync = off != "history";
        server.IsEnabled = off != "server";
        server.UserMappings[0].IsEnabled = off != "user";
        server.LibraryMappings[0].IsEnabled = off != "library";

        Assert.Equal(expected, new Probe(this).Enabled());
    }

    /// <summary>
    /// A server with no enabled user mapping is listed as empty without a request.
    /// True: a source with no mapped users costs no calls.
    /// False: every library of an unmapped source is paged for nothing.
    /// </summary>
    [Fact]
    public async Task GetListAsync_NoUserMappings_ReturnsNothingWithoutAsking()
    {
        var server = AddServer("a", Guid.NewGuid().ToString("N"));
        server.UserMappings[0].IsEnabled = false;

        var work = await new Probe(this).List(Source(server, 0));

        Assert.Empty(work);
        Assert.Empty(_factory.HandlerFor("a").Requests);
    }

    /// <summary>
    /// A mapping whose local library is unset, malformed, or missing gives no work and asks the source for nothing.
    /// True: user data is fetched only for items that exist here.
    /// False: a source library is paged in full although nothing here could match it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("55555555555555555555555555555555")]
    public async Task GetListAsync_LocalLibraryUnusable_ReturnsNothingWithoutAsking(string localLibraryId)
    {
        var server = AddServer("a", Guid.NewGuid().ToString("N"));
        server.LibraryMappings[0].LocalLibraryId = localLibraryId;

        var work = await new Probe(this).List(Source(server, 0));

        Assert.Empty(work);
        Assert.Empty(_factory.HandlerFor("a").Requests);
    }

    /// <summary>
    /// Two servers collide on the local user and the local path the source path translates to.
    /// True: the same film watched by the same local user on two sources is recognised as one file.
    /// False: both servers' history is applied to the file in turn, each undoing the other.
    /// </summary>
    [Fact]
    public void PriorityKeyOf_Work_IsTheLocalUserAndTranslatedPath()
    {
        var a = AddServer("a", Guid.NewGuid().ToString("N"));
        var b = AddServer("b", Guid.NewGuid().ToString("N"));
        var task = new Probe(this);

        var first = task.KeyOf(Offer(a, 0, Guid.NewGuid()));
        var second = task.KeyOf(Offer(b, 1, Guid.NewGuid()));

        Assert.Equal(LocalUser + "|/local/movies/Film/Film.mkv", first!.Replace('\\', '/'));
        Assert.Equal(first, second);
    }

    /// <summary>
    /// An offer with no path, and a row with no local path, have no collision key.
    /// True: an item that cannot be placed never collides with anything.
    /// False: every pathless item shares one key and all but one are dropped.
    /// </summary>
    [Fact]
    public void PriorityKeyOf_NoPath_IsNull()
    {
        var server = AddServer("a", Guid.NewGuid().ToString("N"));
        var task = new Probe(this);
        var offer = Offer(server, 0, Guid.NewGuid());
        offer.SourceItem.Path = null;

        Assert.Null(task.KeyOf(offer));
        Assert.Null(task.KeyOf(new HistorySyncItem { LocalUserId = LocalUser, LocalPath = string.Empty }));
        Assert.Equal(LocalUser + "|/local/a.mkv", task.KeyOf(new HistorySyncItem { LocalUserId = LocalUser, LocalPath = "/local/a.mkv" }));
    }

    /// <summary>
    /// With nothing seen before, every server's history counts as moved, so the highest ranked one speaks.
    /// True: a first scan follows rank.
    /// False: a first scan picks a lower ranked server at random.
    /// </summary>
    [Fact]
    public async Task ContestAsync_NothingSeenBefore_LeavesRankToDecide()
    {
        var a = AddServer("a", Guid.NewGuid().ToString("N"));
        var b = AddServer("b", Guid.NewGuid().ToString("N"));

        var winners = await new Probe(this).Contest(Offer(a, 0, Guid.NewGuid()), Offer(b, 1, Guid.NewGuid()));

        Assert.Empty(winners);
    }

    /// <summary>
    /// When the top server's history is as last seen and a lower server's has moved, the lower server speaks for the file this run.
    /// True: a play on the lower ranked server reaches this server.
    /// False: the top server's unchanged history shadows every play made elsewhere, forever.
    /// </summary>
    [Fact]
    public async Task ContestAsync_OnlyALowerServerMoved_ThatServerSpeaks()
    {
        var a = AddServer("a", Guid.NewGuid().ToString("N"));
        var b = AddServer("b", Guid.NewGuid().ToString("N"));
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();
        Seen(a, itemA);
        Seen(b, itemB);
        var moved = Offer(b, 1, itemB, played: false);

        var winners = await ContestDuringRunAsync(Offer(a, 0, itemA), moved);

        Assert.Same(moved, Assert.Single(winners).Value);
    }

    /// <summary>
    /// When no server's history has moved, or the top server's has, rank decides.
    /// True: an unchanged file stays with its highest ranked server.
    /// False: the speaker for an unchanged file flips between servers on each run.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ContestAsync_TopServerMovedOrNoneMoved_LeavesRankToDecide(bool topMoved)
    {
        var a = AddServer("a", Guid.NewGuid().ToString("N"));
        var b = AddServer("b", Guid.NewGuid().ToString("N"));
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();
        Seen(a, itemA);
        Seen(b, itemB);

        var winners = await ContestDuringRunAsync(Offer(a, 0, itemA, played: !topMoved), Offer(b, 1, itemB));

        Assert.Empty(winners);
    }

    /// <summary>
    /// A history row may outrank its file's owner on a changes only run, and is never retired when another server speaks.
    /// True: each server's last seen state, and any base negotiated with it, survives another server speaking.
    /// False: the row is deleted and the next scan treats old history as new.
    /// </summary>
    [Theory]
    [InlineData(SyncStatus.Synced)]
    [InlineData(SyncStatus.Queued)]
    [InlineData(SyncStatus.Ignored)]
    public void CanRetire_AnyRow_IsFalseAndItMayOutrankItsOwner(SyncStatus status)
    {
        var task = new Probe(this);
        var row = new HistorySyncItem { Status = status };

        Assert.False(task.Retires(row));
        Assert.True(task.Outranks(row));
    }

    /// <summary>
    /// A row's key is its source user and source item.
    /// True: one user's history of one item is one row.
    /// False: two users share a row, or one item gets a row for each scan.
    /// </summary>
    [Fact]
    public void ExtractKey_IsTheSourceUserAndItem()
    {
        var key = new Probe(this).RowKey(new HistorySyncItem { SourceUserId = "u", SourceItemId = "i", LocalUserId = "x" });

        Assert.Equal(("u", "i"), key);
    }

    /// <summary>
    /// Changing which parts of history are synced changes the scan's scope.
    /// True: the next scan reads everything again after a part is turned on or off.
    /// False: rows merged under the old parts are trusted and the change never takes effect.
    /// </summary>
    [Fact]
    public void ScopeSettings_ChangesWithTheSyncedParts()
    {
        var task = new Probe(this);
        var before = task.Scope;

        _config.Configuration.HistorySyncFavorites = !_config.Configuration.HistorySyncFavorites;

        Assert.NotEqual(before, task.Scope);
    }

    /// <summary>
    /// The scan shares the History lock and stamps the History time when it finishes.
    /// True: the scan and the apply never run together, and the dashboard shows the run.
    /// False: a scan overwrites rows the apply just marked Synced.
    /// </summary>
    [Fact]
    public void RecordRunCompleted_StampsTheHistoryTimeUnderTheHistoryLock()
    {
        var task = new Probe(this);
        var config = new PluginConfiguration();

        task.Completed(config, Played);

        Assert.Equal(Played, config.LastHistorySyncTime);
        Assert.Equal("History", task.MutexKey);
        Assert.Equal("ServerSyncRefreshHistoryTable", task.Key);
    }
}
