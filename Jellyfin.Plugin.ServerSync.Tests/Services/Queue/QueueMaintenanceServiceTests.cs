using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Services.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.Playlists;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// One round of upkeep removes what has settled or gone stale, retries what errored, tells the administrator what keeps failing, and never lets one failing step stop the rest.
/// </summary>
[Collection(QueueStateCollection.Name)]
public sealed class QueueMaintenanceServiceTests : IDisposable
{
    private readonly QueueTestDatabase _db = new();
    private readonly FakeConfigManager _config = QueueSamples.Config();
    private readonly HintActivityHarness _activity = new();
    private readonly QueueServices _services = new();
    private readonly Dictionary<Guid, BaseItem> _items = new();
    private readonly Dictionary<Guid, User> _users = new();
    private readonly InterfaceStub _library;
    private readonly OutboundHintStore _outbound;
    private readonly VersionStore _versions;
    private readonly MaintenanceStore _store;
    private readonly HistorySyncTableManager _history;
    private readonly ContentSyncTableManager _content;
    private readonly QueueMaintenanceService _service;
    private readonly string _libraryFolder;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueMaintenanceServiceTests"/> class.
    /// </summary>
    public QueueMaintenanceServiceTests()
    {
        _outbound = new OutboundHintStore(_db.Provider, NullLogger<OutboundHintStore>.Instance);
        _versions = new VersionStore(_db.Provider, NullLogger<VersionStore>.Instance);
        _store = new MaintenanceStore(_db.Provider, NullLogger<MaintenanceStore>.Instance);
        _history = new HistorySyncTableManager(_db.Provider, NullLogger<HistorySyncTableManager>.Instance);
        _content = new ContentSyncTableManager(_db.Provider, NullLogger<ContentSyncTableManager>.Instance);
        _ = new PlaylistCopyStore(_db.Provider, NullLogger<PlaylistCopyStore>.Instance);
        _services.Add(_content);

        var (library, libraryStub) = InterfaceStub.Create<ILibraryManager>();
        libraryStub.Answer(nameof(ILibraryManager.GetItemById), args => _items.TryGetValue((Guid)args[0]!, out var item) ? item : null);
        _library = libraryStub;
        var (users, userStub) = InterfaceStub.Create<IUserManager>();
        userStub.Answer(nameof(IUserManager.GetUserById), args => _users.TryGetValue((Guid)args[0]!, out var user) ? user : null);

        _libraryFolder = Path.Combine(_db.Folder, "library");
        Directory.CreateDirectory(_libraryFolder);
        var server = QueueSamples.Server();
        server.LibraryMappings[0].LocalRootPath = _libraryFolder;
        _config.Configuration.Servers.Add(server);

        _service = new QueueMaintenanceService(_outbound, _versions, _store, _config, library, users, _services, NullLogger<QueueMaintenanceService>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _service.Dispose();
        _activity.Dispose();
        _db.Dispose();
    }

    private Guid ItemHere()
    {
        var id = Guid.NewGuid();
        _items[id] = new Movie { Id = id };
        return id;
    }

    private Guid UserHere()
    {
        var user = new User("viewer-" + Guid.NewGuid().ToString("N"), "Default", "Default");
        _users[user.Id] = user;
        return user.Id;
    }

    private void Stamp(HintKind kind, string key)
        => _versions.Set(new ObjectVersion { Kind = kind, Key = key, ServerId = "peer", Timestamp = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) });

    private HistorySyncItem Errored(string item, DateTime erroredAt) => new()
    {
        SourceUserId = "su",
        LocalUserId = "lu",
        SourceLibraryId = "lib",
        LocalLibraryId = "local",
        SourceItemId = item,
        ItemName = "Item " + item,
        ServerKey = "origin",
        Status = SyncStatus.Errored,
        StatusDate = erroredAt,
        Reason = "the peer refused",
        RetryCount = 3
    };

    /// <summary>Writes a synced film into the library folder and returns its path.</summary>
    private string SyncedFilm(string name = "film.mkv")
    {
        var path = Path.Combine(_libraryFolder, name);
        File.WriteAllText(path, "film");
        _content.Upsert(new SyncItem
        {
            SourceItemId = Guid.NewGuid().ToString("N"),
            SourceLibraryId = "lib",
            LocalLibraryId = "local",
            SourcePath = "/media/movies/" + name,
            LocalPath = path,
            ServerKey = "origin",
            Status = SyncStatus.Synced,
            StatusDate = DateTime.UtcNow
        });
        return path;
    }

    /// <summary>Writes a file beside a film with a last write time that many days ago.</summary>
    private static string Leftover(string path, double daysOld)
    {
        File.WriteAllText(path, "old");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysOld));
        return path;
    }

    /// <summary>
    /// A missing collaborator is refused at construction.
    /// True: a wiring mistake fails at startup.
    /// False: upkeep fails six hours later, where nobody is looking.
    /// </summary>
    [Fact]
    public void Constructor_NullCollaborator_Throws()
    {
        var (library, _) = InterfaceStub.Create<ILibraryManager>();
        var (users, _) = InterfaceStub.Create<IUserManager>();
        var log = NullLogger<QueueMaintenanceService>.Instance;

        Assert.Throws<ArgumentNullException>(() => new QueueMaintenanceService(null!, _versions, _store, _config, library, users, _services, log));
        Assert.Throws<ArgumentNullException>(() => new QueueMaintenanceService(_outbound, null!, _store, _config, library, users, _services, log));
        Assert.Throws<ArgumentNullException>(() => new QueueMaintenanceService(_outbound, _versions, null!, _config, library, users, _services, log));
        Assert.Throws<ArgumentNullException>(() => new QueueMaintenanceService(_outbound, _versions, _store, null!, library, users, _services, log));
        Assert.Throws<ArgumentNullException>(() => new QueueMaintenanceService(_outbound, _versions, _store, _config, null!, users, _services, log));
        Assert.Throws<ArgumentNullException>(() => new QueueMaintenanceService(_outbound, _versions, _store, _config, library, null!, _services, log));
        Assert.Throws<ArgumentNullException>(() => new QueueMaintenanceService(_outbound, _versions, _store, _config, library, users, null!, log));
        Assert.Throws<ArgumentNullException>(() => new QueueMaintenanceService(_outbound, _versions, _store, _config, library, users, _services, null!));
    }

    /// <summary>
    /// A hint that has sat sent for over a week is removed, and one sent more recently is kept.
    /// True: rows a peer never completed do not stay forever, and recent ones still get their completion.
    /// False: the queue grows for good, or a row is removed before the peer could finish it.
    /// </summary>
    [Fact]
    public void RunOnce_HintSentOverAWeekAgo_IsExpired()
    {
        var old = new OutboundHint { PeerKey = "origin", Kind = HintKind.Metadata, Key = "old", VersionServerId = "me", VersionTimestamp = DateTime.UtcNow.AddDays(-9) };
        var recent = new OutboundHint { PeerKey = "origin", Kind = HintKind.Metadata, Key = "recent", VersionServerId = "me", VersionTimestamp = DateTime.UtcNow.AddDays(-9) };
        _outbound.Enqueue(old, "me");
        _outbound.Enqueue(recent, "me");
        _outbound.MarkSent(new[] { old }, DateTime.UtcNow.AddDays(-8));
        _outbound.MarkSent(new[] { recent }, DateTime.UtcNow.AddDays(-6));

        _service.RunOnce();

        Assert.Equal("recent", Assert.Single(_outbound.GetAll()).Key);
    }

    /// <summary>
    /// A row that errored over an hour ago is queued again with fresh retries, and one that errored just now is not.
    /// True: what failed while a peer was away is tried again by itself.
    /// False: errored rows wait for a person, or a row that failed a moment ago is retried at once.
    /// </summary>
    [Fact]
    public void RunOnce_RowErroredOverAnHourAgo_IsQueuedAgain()
    {
        _history.Upsert(Errored("old", DateTime.UtcNow.AddHours(-2)));
        _history.Upsert(Errored("recent", DateTime.UtcNow));

        _service.RunOnce();

        var old = _history.GetByKey(("su", "old"));
        Assert.Equal(SyncStatus.Queued, old?.Status);
        Assert.Equal(0, old?.RetryCount);
        Assert.Equal(SyncStatus.Errored, _history.GetByKey(("su", "recent"))?.Status);
    }

    /// <summary>
    /// Stamps of items and users that no longer exist are removed, and those of what still exists are kept.
    /// True: the stamp table follows the library.
    /// False: stamps of deleted items pile up, or a live item loses its stamp and the next conflict goes the wrong way.
    /// </summary>
    [Fact]
    public void RunOnce_StampsOfMissingItemsAndUsers_ArePruned()
    {
        var item = ItemHere();
        var user = UserHere();
        var goneItem = Guid.NewGuid();
        var goneUser = Guid.NewGuid();
        Stamp(HintKind.Metadata, HintProtocol.MetadataKey(item));
        Stamp(HintKind.Metadata, HintProtocol.MetadataKey(goneItem));
        Stamp(HintKind.Content, HintProtocol.MetadataKey(goneItem));
        Stamp(HintKind.History, HintProtocol.HistoryKey(user, item));
        Stamp(HintKind.History, HintProtocol.HistoryKey(goneUser, item));
        Stamp(HintKind.History, HintProtocol.HistoryKey(user, goneItem));
        Stamp(HintKind.Users, HintProtocol.UsersKey(user));
        Stamp(HintKind.Users, HintProtocol.UsersKey(goneUser));

        _service.RunOnce();

        Assert.Equal(new[] { HintProtocol.MetadataKey(item) }, _versions.GetKeys(HintKind.Metadata));
        Assert.Empty(_versions.GetKeys(HintKind.Content));
        Assert.Equal(new[] { HintProtocol.HistoryKey(user, item) }, _versions.GetKeys(HintKind.History));
        Assert.Equal(new[] { HintProtocol.UsersKey(user) }, _versions.GetKeys(HintKind.Users));
    }

    /// <summary>
    /// Stamps of people, and stamps under keys that are not ids, are never pruned.
    /// True: a person who is not scanned in yet keeps the stamp that defends their edit.
    /// False: a people edit loses its stamp every six hours and an older value overwrites it.
    /// </summary>
    [Fact]
    public void RunOnce_StampsOfPeopleAndOfKeysThatAreNotIds_AreKept()
    {
        Stamp(HintKind.People, "JANE DOE");
        Stamp(HintKind.Metadata, "not-an-id");
        Stamp(HintKind.History, HintProtocol.HistoryPartKey("not-a-key", HistoryParts.Played));

        _service.RunOnce();

        Assert.Single(_versions.GetKeys(HintKind.People));
        Assert.Single(_versions.GetKeys(HintKind.Metadata));
        Assert.Single(_versions.GetKeys(HintKind.History));
    }

    /// <summary>
    /// The registration of a copied playlist that no longer exists is forgotten, and a live one is kept.
    /// True: a deleted copy leaves no entry that could mark a new playlist as a copy.
    /// False: dead registrations pile up, or a live copy is forgotten and copied a second time.
    /// </summary>
    [Fact]
    public void RunOnce_CopyOfAPlaylistThatIsGone_IsForgotten()
    {
        var live = Guid.NewGuid();
        var gone = Guid.NewGuid();
        _items[live] = new Playlist { Id = live };
        PlaylistCopyStore.Set(live, Guid.NewGuid().ToString("N"), "origin");
        PlaylistCopyStore.Set(gone, Guid.NewGuid().ToString("N"), "origin");

        _service.RunOnce();

        Assert.NotNull(PlaylistCopyStore.Get(live));
        Assert.Null(PlaylistCopyStore.Get(gone));
    }

    /// <summary>
    /// A replacement backup and a half downloaded file left for over a day are removed, and fresh ones are kept.
    /// True: leftovers of a finished or abandoned transfer do not fill the disk, and one still in use is untouched.
    /// False: every replaced film leaves a second copy forever, or a download in progress loses its file.
    /// </summary>
    [Fact]
    public void RunOnce_LeftoversOlderThanADay_AreRemovedAndFreshOnesKept()
    {
        var film = SyncedFilm();
        var staged = new string('a', 32);
        var oldBackup = Leftover(film + ".replacing.old", daysOld: 2);
        var freshBackup = Leftover(film + ".replacing.fresh", daysOld: 0.5);
        var oldStaged = Leftover(film + ".syncing." + staged, daysOld: 2);
        var freshStaged = Leftover(film + ".syncing." + new string('b', 32), daysOld: 0.5);
        var notOurs = Leftover(film + ".syncing.notes", daysOld: 2);

        _service.RunOnce();

        Assert.False(File.Exists(oldBackup));
        Assert.False(File.Exists(oldStaged));
        Assert.True(File.Exists(freshBackup));
        Assert.True(File.Exists(freshStaged));
        Assert.True(File.Exists(notOurs));
        Assert.True(File.Exists(film));
    }

    /// <summary>
    /// Leftovers outside every mapped library are never removed.
    /// True: upkeep deletes only inside folders Server Sync manages.
    /// False: a row pointing at an unmanaged folder lets upkeep delete files there.
    /// </summary>
    [Fact]
    public void RunOnce_LeftoversOutsideEveryMappedLibrary_AreKept()
    {
        var film = SyncedFilm();
        var backup = Leftover(film + ".replacing.old", daysOld: 2);
        _config.Configuration.Servers[0].LibraryMappings[0].LocalRootPath = Path.Combine(_db.Folder, "another-library");

        _service.RunOnce();

        Assert.True(File.Exists(backup));
    }

    /// <summary>
    /// A step that throws does not stop the steps after it.
    /// True: a library that cannot be read costs one step, and the rest of the upkeep still runs.
    /// False: one failing step leaves backups, errored rows, and old hints untouched for good.
    /// </summary>
    [Fact]
    public void RunOnce_AStepThrows_TheLaterStepsStillRun()
    {
        var film = SyncedFilm();
        var backup = Leftover(film + ".replacing.old", daysOld: 2);
        Stamp(HintKind.Metadata, HintProtocol.MetadataKey(Guid.NewGuid()));
        _library.Answer(nameof(ILibraryManager.GetItemById), _ => throw new InvalidOperationException("the library is not ready"));

        _service.RunOnce();

        Assert.Single(_versions.GetKeys(HintKind.Metadata));
        Assert.False(File.Exists(backup));
    }

    /// <summary>
    /// Rows that have failed for a day are reported to the Activity page once.
    /// True: the administrator hears of a standing failure, one time.
    /// False: nobody is told, or the same entry is written every six hours.
    /// </summary>
    [Fact]
    public void RunOnce_RowsFailingForADay_AreReportedOnce()
    {
        _services.Add<ActivityLogger>(_activity.Logger);
        _history.Upsert(Errored("stuck", DateTime.UtcNow));
        _store.TrackFailures(DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-3));

        _service.RunOnce();
        _service.RunOnce();

        Assert.Single(_activity.Types, type => type == "ServerSyncAttention");
    }

    /// <summary>
    /// A run that has ended the same wrong way for a day is reported once and marked as reported.
    /// True: an unreachable server is raised to the administrator, one time.
    /// False: the failure stays on the dashboard only, or is raised on every upkeep.
    /// </summary>
    [Fact]
    public void RunOnce_RunHeldUpForADay_IsReportedOnceAndMarked()
    {
        _services.Add<ActivityLogger>(_activity.Logger);
        var standing = new SyncRunFailure { ModuleKey = "Content", Phase = "Refresh", Reason = "the server did not answer", Timestamp = DateTime.UtcNow, FirstSeen = DateTime.UtcNow.AddDays(-2) };
        var recent = new SyncRunFailure { ModuleKey = "History", Phase = "Sync", Reason = "the server did not answer", Timestamp = DateTime.UtcNow, FirstSeen = DateTime.UtcNow.AddHours(-1) };
        _config.Configuration.LastRunFailures.Add(standing);
        _config.Configuration.LastRunFailures.Add(recent);

        _service.RunOnce();
        _service.RunOnce();

        Assert.True(standing.Reported);
        Assert.False(recent.Reported);
        Assert.Equal(1, _config.Saves);
        Assert.Single(_activity.Types, type => type == "ServerSyncAttention");
    }

    /// <summary>
    /// With nothing to do, a round of upkeep changes nothing and does not throw.
    /// True: upkeep on a quiet server is harmless.
    /// False: an empty table or a missing optional service fails the round.
    /// </summary>
    [Fact]
    public void RunOnce_NothingToDo_ChangesNothing()
    {
        _service.RunOnce();

        Assert.Empty(_outbound.GetAll());
        Assert.Equal(0, _config.Saves);
        Assert.Empty(_activity.Types);
    }

    /// <summary>
    /// The service starts, stops before its first run, and can be stopped or disposed without ever starting.
    /// True: a server that shuts down soon after starting does not hang on upkeep.
    /// False: shutdown waits out the first delay, or disposing an unstarted service throws.
    /// </summary>
    [Fact]
    public async Task StartAndStop_BeforeTheFirstRun_EndCleanly()
    {
        var (library, _) = InterfaceStub.Create<ILibraryManager>();
        var (users, _) = InterfaceStub.Create<IUserManager>();
        using var neverStarted = new QueueMaintenanceService(_outbound, _versions, _store, _config, library, users, _services, NullLogger<QueueMaintenanceService>.Instance);
        Stamp(HintKind.Metadata, HintProtocol.MetadataKey(Guid.NewGuid()));

        await neverStarted.StopAsync(CancellationToken.None);
        await _service.StartAsync(CancellationToken.None);
        await _service.StopAsync(CancellationToken.None);

        Assert.Single(_versions.GetKeys(HintKind.Metadata));
    }
}
