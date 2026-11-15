using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tasks.MetadataSync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Tasks.MetadataSync;

/// <summary>
/// The metadata apply writes parents before children, applies only the categories that are on, and marks a category synced only after it reads back as the source's.
/// </summary>
public sealed class SyncMissingMetadataTaskTests : IDisposable
{
    private const string ServerKey = "server-a";
    private const string CastFromSource = "[{\"Name\":\"Alice Smith\",\"Role\":\"Lead\",\"Type\":\"Actor\"}]";

    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly MetadataSyncTableManager _manager;
    private readonly FakeConfigManager _config = new();
    private readonly TaskClientFactory _factory = new();
    private readonly SourceServer _server;
    private readonly Dictionary<Guid, BaseItem> _itemsById = new();
    private readonly InterfaceStub _libraryStub;
    private readonly ILibraryManager _library;
    private readonly MetadataSyncTableService _service;
    private readonly Movie _movie = new() { Id = Guid.NewGuid(), Name = "Film", Studios = new[] { "Pixar" } };

    public SyncMissingMetadataTaskTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-metadata-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        _manager = new MetadataSyncTableManager(new FixedProvider(_database), NullLogger<MetadataSyncTableManager>.Instance);
        (_library, _libraryStub) = InterfaceStub.Create<ILibraryManager>();
        _libraryStub.Answer(nameof(ILibraryManager.GetItemById), args => _itemsById.GetValueOrDefault((Guid)args[0]!));
        _itemsById[_movie.Id] = _movie;
        _service = new MetadataSyncTableService(NullLogger<MetadataSyncTableService>.Instance, _library, InterfaceStub.Create<ILocalItems>().Proxy, new NoLinks());
        _server = new SourceServer
        {
            Key = ServerKey,
            Name = "Source A",
            Url = "http://server-a:8096",
            ApiKey = "k",
            LibraryMappings = { new LibraryMapping { SourceLibraryId = "lib-1", LocalRootPath = "/local" } }
        };
        _config.Configuration.Servers.Add(_server);
        _config.Configuration.EnableMetadataSync = true;
        SetCategories(metadata: true, images: true, people: true, studios: true);
    }

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class Probe : SyncMissingMetadataTask
    {
        public Probe(SyncMissingMetadataTaskTests test)
            : base(NullLogger<SyncMissingMetadataTask>.Instance, test._library, InterfaceStub.Create<IProviderManager>().Proxy, test._factory, test._config, test._service, test._manager, null!)
        {
        }

        public string MutexKey => ModuleMutexKey;

        public int Parallelism => MaxDegreeOfParallelism;

        public bool Enabled() => IsEnabled();

        public IList<MetadataSyncItem> ToApply() => GetItemsToApply();

        public List<IList<MetadataSyncItem>> Groups(IList<MetadataSyncItem> items) => GetApplyGroups(items).ToList();

        public Task Apply(MetadataSyncItem record) => ApplyAsync(record, CancellationToken.None);

        public Task Verify(MetadataSyncItem record) => VerifyAfterApplyAsync(record, CancellationToken.None);

        public void Completed(PluginConfiguration config, DateTime at) => RecordRunCompleted(config, at);
    }

    private Probe NewTask() => new(this);

    private void SetCategories(bool metadata, bool images, bool people, bool studios)
    {
        var config = _config.Configuration;
        config.MetadataSyncMetadata = metadata;
        config.MetadataSyncImages = images;
        config.MetadataSyncPeople = people;
        config.MetadataSyncStudios = studios;
    }

    private MetadataSyncItem Row(string? itemType = "Movie", string? name = "Film", string? sourceItemId = null) => new()
    {
        SourceLibraryId = "lib-1",
        SourceItemId = sourceItemId ?? Guid.NewGuid().ToString("N"),
        LocalItemId = _movie.Id.ToString("N"),
        ItemName = name,
        ItemType = itemType,
        ServerKey = ServerKey,
        Status = SyncStatus.Queued
    };

    private MetadataSyncItem RowWithStudios(string sourceStudios)
    {
        var row = Row();
        row.Studios.UpdateSource(sourceStudios);
        row.Studios.Local = "[\"Somewhere Else\"]";
        return row;
    }

    /// <summary>
    /// The task runs when Metadata is on, a source and a library mapping exist, and a category it can apply is on.
    /// True: queued metadata is applied whenever something could be written.
    /// False: the queue never drains, or the task connects to sources with nothing to write.
    /// </summary>
    [Theory]
    [InlineData("none", true)]
    [InlineData("sync", false)]
    [InlineData("server", false)]
    [InlineData("mapping", false)]
    [InlineData("categories", false)]
    public void IsEnabled_NeedsMetadataASourceAMappingAndACategory(string off, bool expected)
    {
        _config.Configuration.EnableMetadataSync = off != "sync";
        _server.IsEnabled = off != "server";
        _server.LibraryMappings[0].IsEnabled = off != "mapping";
        if (off == "categories")
        {
            SetCategories(false, false, false, false);
        }

        Assert.Equal(expected, NewTask().Enabled());
    }

    /// <summary>
    /// The queued rows come back parents first, by name within a tier, and rows that are not queued are left out.
    /// True: a series is applied before its seasons, and those before episodes and films.
    /// False: a child is written first and its parent's apply then overwrites what it inherited.
    /// </summary>
    [Fact]
    public void GetItemsToApply_QueuedRows_AreOrderedParentsFirstThenByName()
    {
        _manager.Upsert(Row("Movie", "beta"));
        _manager.Upsert(Row("Season", "m"));
        _manager.Upsert(Row("Series", "z"));
        _manager.Upsert(Row("Episode", "Alpha"));
        _manager.Upsert(Row("BoxSet", "c"));
        _manager.Upsert(Row("MusicArtist", "y"));
        _manager.Upsert(Row(null, "gamma"));
        var synced = Row("Series", "a");
        synced.Status = SyncStatus.Synced;
        _manager.Upsert(synced);

        var names = NewTask().ToApply().Select(r => r.ItemName).ToArray();

        Assert.Equal(new[] { "y", "z", "c", "m", "Alpha", "beta", "gamma" }, names);
    }

    /// <summary>
    /// The rows are grouped into the three tiers, applied in order.
    /// True: a tier finishes before the next starts, even with four rows applied at once.
    /// False: a season and its series are applied side by side.
    /// </summary>
    [Fact]
    public void GetApplyGroups_Rows_AreGroupedByTierInOrder()
    {
        var rows = new List<MetadataSyncItem>
        {
            Row("Movie", "film"),
            Row("MusicAlbum", "album"),
            Row("Series", "series"),
            Row("Season", "season"),
            Row("MusicArtist", "artist")
        };
        var task = NewTask();

        var groups = task.Groups(rows);

        Assert.Equal(3, groups.Count);
        Assert.Equal(new[] { "series", "artist" }, groups[0].Select(r => r.ItemName).ToArray());
        Assert.Equal(new[] { "album", "season" }, groups[1].Select(r => r.ItemName).ToArray());
        Assert.Equal(new[] { "film" }, groups[2].Select(r => r.ItemName).ToArray());
        Assert.Empty(task.Groups(new List<MetadataSyncItem>()));
        Assert.Equal(4, task.Parallelism);
    }

    /// <summary>
    /// A row with no local item, an id that is not an id, or an item the library no longer has is refused with the reason.
    /// True: a row whose local item is gone errors with a cause the table shows.
    /// False: the apply fails with a null reference, or writes to another item.
    /// </summary>
    [Theory]
    [InlineData(null, "Local item not found")]
    [InlineData("", "Local item not found")]
    [InlineData("not-an-id", "Invalid item ID")]
    [InlineData("99999999999999999999999999999999", "Local item not found in library")]
    public async Task ApplyAsync_UnusableLocalItem_ThrowsTheReason(string? localItemId, string reason)
    {
        var row = Row();
        row.LocalItemId = localItemId;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => NewTask().Apply(row));

        Assert.Equal(reason, thrown.Message);
    }

    /// <summary>
    /// Studios that already match in order and case are not written again.
    /// True: an unchanged item is not saved, so Jellyfin raises no change for it.
    /// False: every run saves every queued item and echoes a change to every peer.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_StudiosAlreadyMatch_WritesNothing()
    {
        var before = _movie.Studios;

        var thrown = await Record.ExceptionAsync(() => NewTask().Apply(RowWithStudios("[\"Pixar\"]")));

        Assert.Null(thrown);
        Assert.Same(before, _movie.Studios);
    }

    /// <summary>
    /// Studios that differ are written onto the local item, and the only failure the apply may report for them is the save.
    /// True: the source's studios reach the item, and a save that fails is reported as a save failure.
    /// False: the studios are never written, or the write itself is reported as failed.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_StudiosDiffer_WritesThemOntoTheItem()
    {
        var thrown = await Record.ExceptionAsync(() => NewTask().Apply(RowWithStudios("[\"Disney\",\"Pixar\"]")));

        Assert.Equal(new[] { "Disney", "Pixar" }, _movie.Studios);
        Assert.True(thrown is null || thrown.Message.StartsWith("Persist: UpdateToRepositoryAsync threw", StringComparison.Ordinal), thrown?.Message);
    }

    /// <summary>
    /// A category that is off is not applied, whatever the row holds.
    /// True: turning Studios off leaves local studios alone.
    /// False: a category keeps being written after the administrator turned it off.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_CategoryOff_IsNotApplied()
    {
        SetCategories(metadata: true, images: true, people: true, studios: false);

        await NewTask().Apply(RowWithStudios("[\"Disney\"]"));

        Assert.Equal(new[] { "Pixar" }, _movie.Studios);
    }

    /// <summary>
    /// A cast change for an item that takes no cast fails with the item's type.
    /// True: the row errors saying why the cast could not be written.
    /// False: the write is dropped by Jellyfin and the row is queued again on every scan.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_CastForAnItemThatTakesNone_ThrowsNamingTheType()
    {
        var person = new Person { Id = Guid.NewGuid(), Name = "Alice Smith" };
        _itemsById[person.Id] = person;
        var row = Row();
        row.LocalItemId = person.Id.ToString("N");
        row.People.UpdateSource(CastFromSource);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => NewTask().Apply(row));

        Assert.Equal("People: item type Person does not accept manual people writes", thrown.Message);
    }

    /// <summary>
    /// An image change whose server did not connect fails naming the server, and other categories are still applied.
    /// True: one category failing does not stop the rest, and the reason points at the server.
    /// False: the images are skipped silently, or the whole row is abandoned at the first failure.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_ImagesWithoutTheirServer_ThrowsAndStillAppliesTheRest()
    {
        var row = RowWithStudios("[\"Pixar\"]");
        row.Images.UpdateSource("{\"Primary\":[{\"ImageType\":\"Primary\",\"ImageIndex\":0,\"ImageTag\":\"tag\"}]}");
        row.People.UpdateSource(CastFromSource);
        var person = new Person { Id = Guid.NewGuid(), Name = "Alice Smith", Studios = new[] { "Old Studio" } };
        _itemsById[person.Id] = person;
        row.LocalItemId = person.Id.ToString("N");
        SetCategories(metadata: false, images: true, people: true, studios: true);

        var thrown = await Record.ExceptionAsync(() => NewTask().Apply(row));

        Assert.Equal(new[] { "Pixar" }, person.Studios);
        Assert.StartsWith(
            "Images: apply threw, Source server 'Source A' is not available this run; People: item type Person does not accept manual people writes",
            thrown!.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A row with nothing to apply is not read back.
    /// True: a row queued for nothing costs no library read and passes.
    /// False: an unchanged row errors because its local item cannot be found.
    /// </summary>
    [Fact]
    public async Task VerifyAfterApplyAsync_NothingApplied_ReadsNothing()
    {
        var row = Row();
        row.LocalItemId = "not-an-id";

        var thrown = await Record.ExceptionAsync(() => NewTask().Verify(row));

        Assert.Null(thrown);
        Assert.Empty(_libraryStub.Calls);
    }

    /// <summary>
    /// An item that is gone, or an id that is not an id, fails the check.
    /// True: an item deleted during the apply errors the row.
    /// False: the check passes against nothing and the row is marked Synced.
    /// </summary>
    [Theory]
    [InlineData("not-an-id", "Invalid item ID")]
    [InlineData("99999999999999999999999999999999", "Verify: local item disappeared after apply")]
    public async Task VerifyAfterApplyAsync_LocalItemUnusable_Throws(string localItemId, string reason)
    {
        var row = RowWithStudios("[\"Disney\"]");
        row.LocalItemId = localItemId;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => NewTask().Verify(row));

        Assert.Equal(reason, thrown.Message);
    }

    /// <summary>
    /// Studios that read back as the source's, in any order or case, are marked synced.
    /// True: an applied category is recorded and not queued again.
    /// False: a difference in case alone keeps the row Errored forever.
    /// </summary>
    [Fact]
    public async Task VerifyAfterApplyAsync_StudiosLanded_MarksStudiosSynced()
    {
        _movie.Studios = new[] { "pixar", "DISNEY" };
        var row = RowWithStudios("[\"Disney\",\"Pixar\"]");

        await NewTask().Verify(row);

        Assert.Equal(row.Studios.SourceHash, row.Studios.SyncedHash);
        Assert.Equal(row.Studios.Source, row.Studios.Synced);
        Assert.Null(row.Metadata.SyncedHash);
    }

    /// <summary>
    /// Studios that do not read back as the source's fail the check with both lists, and are not marked synced.
    /// True: a studio a provider rewrote is caught and shown in the reason.
    /// False: the row is marked Synced while the item still has the old studios.
    /// </summary>
    [Fact]
    public async Task VerifyAfterApplyAsync_StudiosDidNotLand_ThrowsAndLeavesThemUnsynced()
    {
        var row = RowWithStudios("[\"Disney\"]");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => NewTask().Verify(row));

        Assert.Equal("Studios: studios mismatch: source has 1 (Disney), local has 1 (Pixar)", thrown.Message);
        Assert.Null(row.Studios.SyncedHash);
    }

    /// <summary>
    /// A category that is off is not checked, so a difference in it does not fail the row.
    /// True: turning a category off stops it erroring rows.
    /// False: rows stay Errored over a category nobody is syncing.
    /// </summary>
    [Fact]
    public async Task VerifyAfterApplyAsync_CategoryOff_IsNotChecked()
    {
        SetCategories(metadata: true, images: true, people: true, studios: false);
        var row = RowWithStudios("[\"Disney\"]");

        var thrown = await Record.ExceptionAsync(() => NewTask().Verify(row));

        Assert.Null(thrown);
        Assert.Null(row.Studios.SyncedHash);
    }

    /// <summary>
    /// A finished run stamps the Metadata time, under the lock the Metadata scan shares.
    /// True: the dashboard shows when Metadata last ran, and the scan and the apply never overlap.
    /// False: another sync type's time moves, or a scan overwrites a row mid apply.
    /// </summary>
    [Fact]
    public void RecordRunCompleted_StampsTheMetadataTimeUnderTheMetadataLock()
    {
        var task = NewTask();
        var config = new PluginConfiguration();
        var at = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        task.Completed(config, at);

        Assert.Equal(at, config.LastMetadataSyncTime);
        Assert.Null(config.LastPeopleSyncTime);
        Assert.Equal("Metadata", task.MutexKey);
        Assert.Equal("ServerSyncMissingMetadata", task.Key);
    }
}
