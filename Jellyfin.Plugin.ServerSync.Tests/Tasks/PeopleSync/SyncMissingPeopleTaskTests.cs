using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Tasks.PeopleSync;

/// <summary>
/// The people apply finds the local person without ever creating one, writes only what changed, and marks a category synced only after it reads back as the source's.
/// </summary>
public sealed class SyncMissingPeopleTaskTests : IDisposable
{
    private const string ServerKey = "server-a";
    private const string ImagesFromSource = "{\"Primary\":[{\"ImageType\":\"Primary\",\"ImageIndex\":0,\"ImageTag\":\"tag\"}]}";

    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly PeopleSyncTableManager _manager;
    private readonly FakeConfigManager _config = new();
    private readonly TaskClientFactory _factory = new();
    private readonly SourceServer _server;
    private readonly Dictionary<Guid, BaseItem> _itemsById = new();
    private readonly List<BaseItem> _nameHits = new();
    private readonly InterfaceStub _libraryStub;
    private readonly ILibraryManager _library;
    private readonly Person _person = new() { Id = Guid.NewGuid(), Name = "Alice Smith", Overview = "local text" };

    public SyncMissingPeopleTaskTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-people-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        _manager = new PeopleSyncTableManager(new FixedProvider(_database), NullLogger<PeopleSyncTableManager>.Instance);
        _server = new SourceServer { Key = ServerKey, Name = "Source A", Url = "http://server-a:8096", ApiKey = "k" };
        _config.Configuration.Servers.Add(_server);
        _config.Configuration.EnablePeopleSync = true;
        _config.Configuration.PeopleSyncImages = true;
        (_library, _libraryStub) = InterfaceStub.Create<ILibraryManager>();
        _libraryStub.Answer(nameof(ILibraryManager.GetItemById), args => _itemsById.GetValueOrDefault((Guid)args[0]!));
        _libraryStub.Answer(nameof(ILibraryManager.GetItemList), _ => _nameHits);
        _itemsById[_person.Id] = _person;
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

    private sealed class Probe : SyncMissingPeopleTask
    {
        public Probe(SyncMissingPeopleTaskTests test)
            : base(NullLogger<SyncMissingPeopleTask>.Instance, test._library, InterfaceStub.Create<IProviderManager>().Proxy, test._factory, test._config, test._manager, null!)
        {
        }

        public string MutexKey => ModuleMutexKey;

        public bool Enabled() => IsEnabled();

        public Task Apply(PeopleSyncItem record) => ApplyAsync(record, CancellationToken.None);

        public Task Verify(PeopleSyncItem record) => VerifyAfterApplyAsync(record, CancellationToken.None);

        public void Completed(PluginConfiguration config, DateTime at) => RecordRunCompleted(config, at);
    }

    private Probe NewTask() => new(this);

    private PeopleSyncItem Row(bool metadataDiffers = false)
    {
        var row = new PeopleSyncItem
        {
            PersonName = "Alice Smith",
            LocalPersonId = _person.Id.ToString("N"),
            SourcePersonId = Guid.NewGuid().ToString("N"),
            ServerKey = ServerKey,
            Status = SyncStatus.Queued
        };
        row.Metadata.Local = PeopleSyncMergeService.BuildLocalMetadata(_person);
        row.Metadata.UpdateSource(metadataDiffers
            ? row.Metadata.Local.Replace("local text", "from source", StringComparison.Ordinal)
            : row.Metadata.Local);
        return row;
    }

    private int NameQueries => _libraryStub.Calls.Count(c => c.Name == nameof(ILibraryManager.GetItemList));

    /// <summary>
    /// The task runs only with People on and a source configured.
    /// True: turning People off stops queued people changes from being applied.
    /// False: people keep changing after the sync type was turned off.
    /// </summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void IsEnabled_NeedsPeopleAndASource(bool peopleOn, bool hasServer, bool expected)
    {
        _config.Configuration.EnablePeopleSync = peopleOn;
        _server.IsEnabled = hasServer;

        Assert.Equal(expected, NewTask().Enabled());
    }

    /// <summary>
    /// A row whose stored id is a person is applied to that person without a name query.
    /// True: the person the scan matched is the one written.
    /// False: every apply queries the library by name, and may land on a namesake.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_StoredIdIsAPerson_UsesItWithoutANameQuery()
    {
        await NewTask().Apply(Row());

        Assert.Equal(0, NameQueries);
    }

    /// <summary>
    /// A row whose stored id is missing, malformed, or not a person falls back to an exact name match.
    /// True: a person Jellyfin rebuilt under a new id is still found.
    /// False: the row errors forever after a library rescan, or writes to an item that is not a person.
    /// </summary>
    [Theory]
    [InlineData("gone")]
    [InlineData("malformed")]
    [InlineData("movie")]
    public async Task ApplyAsync_StoredIdUnusable_FallsBackToTheName(string shape)
    {
        var row = Row();
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Alice Smith" };
        _itemsById[movie.Id] = movie;
        row.LocalPersonId = shape switch
        {
            "gone" => Guid.NewGuid().ToString("N"),
            "malformed" => "not-an-id",
            _ => movie.Id.ToString("N")
        };
        _nameHits.Add(_person);

        var thrown = await Record.ExceptionAsync(() => NewTask().Apply(row));

        Assert.Null(thrown);
        Assert.Equal(1, NameQueries);
    }

    /// <summary>
    /// A row with no usable id and no exact name match is refused, and no person is created for it.
    /// True: a person this server no longer has errors the row with their name.
    /// False: a blank person is created, or a namesake is overwritten.
    /// </summary>
    [Theory]
    [InlineData("Alice Smith", "Alice Smithson", "Local person not found: Alice Smith")]
    [InlineData("Alice Smith", null, "Local person not found: Alice Smith")]
    [InlineData("", null, "Local person not found: the row has no person name")]
    public async Task ApplyAsync_NoLocalPerson_ThrowsWithoutCreatingOne(string rowName, string? hitName, string reason)
    {
        var row = Row();
        row.PersonName = rowName;
        row.LocalPersonId = null;
        if (hitName is not null)
        {
            _nameHits.Add(new Person { Id = Guid.NewGuid(), Name = hitName });
        }

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => NewTask().Apply(row));

        Assert.Equal(reason, thrown.Message);
        Assert.DoesNotContain(_libraryStub.Calls, c => c.Name == nameof(ILibraryManager.GetPerson));
    }

    /// <summary>
    /// A metadata change is written onto the local person, and the only failure the apply may report for it is the save.
    /// True: the source's text reaches the person, and a save that fails is reported as a save failure.
    /// False: the change is never written, or the write itself is reported as failed.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_MetadataChanged_WritesTheValueOntoThePerson()
    {
        var row = Row(metadataDiffers: true);

        var thrown = await Record.ExceptionAsync(() => NewTask().Apply(row));

        Assert.Equal("from source", _person.Overview);
        Assert.True(thrown is null || thrown.Message.StartsWith("Persist: UpdateToRepositoryAsync threw", StringComparison.Ordinal), thrown?.Message);
    }

    /// <summary>
    /// An image change with a source person id that is not an id, or whose server did not connect, fails with the reason.
    /// True: the row errors saying why its images could not be fetched.
    /// False: the images are silently skipped and the row is marked Synced.
    /// </summary>
    [Theory]
    [InlineData("not-an-id", "Images: invalid source person ID not-an-id")]
    [InlineData("aaaaaaaa111100000000000000000001", "Images: source server is not available this run")]
    public async Task ApplyAsync_ImagesCannotBeFetched_ThrowsTheReason(string sourcePersonId, string reason)
    {
        var row = Row();
        row.SourcePersonId = sourcePersonId;
        row.Images.UpdateSource(ImagesFromSource);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => NewTask().Apply(row));

        Assert.Equal(reason, thrown.Message);
    }

    /// <summary>
    /// With image sync off, an image difference is not applied.
    /// True: turning images off leaves local person images alone.
    /// False: images keep being replaced after the setting was turned off.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_ImageSyncOff_IgnoresAnImageDifference()
    {
        _config.Configuration.PeopleSyncImages = false;
        var row = Row();
        row.SourcePersonId = "not-an-id";
        row.Images.UpdateSource(ImagesFromSource);

        var thrown = await Record.ExceptionAsync(() => NewTask().Apply(row));

        Assert.Null(thrown);
    }

    /// <summary>
    /// A row with nothing to apply is not read back.
    /// True: a row queued for nothing costs no library read.
    /// False: an unchanged row fails its check because the person cannot be found.
    /// </summary>
    [Fact]
    public async Task VerifyAfterApplyAsync_NothingApplied_ReadsNothing()
    {
        _itemsById.Clear();

        var thrown = await Record.ExceptionAsync(() => NewTask().Verify(Row()));

        Assert.Null(thrown);
        Assert.Empty(_libraryStub.Calls);
    }

    /// <summary>
    /// Metadata that reads back as the source's is marked synced, and the row's local snapshot is refreshed.
    /// True: an applied change is recorded and not queued again by the next scan.
    /// False: the same change is applied on every run.
    /// </summary>
    [Fact]
    public async Task VerifyAfterApplyAsync_MetadataLanded_MarksMetadataSynced()
    {
        var row = Row(metadataDiffers: true);
        _person.Overview = "from source";

        await NewTask().Verify(row);

        Assert.Equal(row.Metadata.SourceHash, row.Metadata.SyncedHash);
        Assert.Equal(row.Metadata.Source, row.Metadata.Synced);
        Assert.False(row.HasMetadataChanges);
    }

    /// <summary>
    /// Metadata that does not read back as the source's fails the check, naming the field, and is not marked synced.
    /// True: a value a metadata provider rewrote is caught and the row errors with the field's name.
    /// False: the row is marked Synced while the person still shows the old text.
    /// </summary>
    [Fact]
    public async Task VerifyAfterApplyAsync_MetadataDidNotLand_ThrowsAndLeavesItUnsynced()
    {
        var row = Row(metadataDiffers: true);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => NewTask().Verify(row));

        Assert.StartsWith("Metadata: verification found", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("[Overview]", thrown.Message, StringComparison.Ordinal);
        Assert.Null(row.Metadata.SyncedHash);
    }

    /// <summary>
    /// A person found by name who is gone when read back by id fails the check.
    /// True: a person deleted during the apply errors the row.
    /// False: the check passes against nothing.
    /// </summary>
    [Fact]
    public async Task VerifyAfterApplyAsync_PersonGoneAfterApply_Throws()
    {
        var row = Row(metadataDiffers: true);
        _itemsById.Clear();
        _nameHits.Add(_person);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => NewTask().Verify(row));

        Assert.Equal("Verify: person entity disappeared after apply", thrown.Message);
    }

    /// <summary>
    /// A finished run stamps the People time, under the lock the People scan shares.
    /// True: the dashboard shows when People last ran, and the scan and the apply never overlap.
    /// False: another sync type's time moves, or a scan overwrites a row mid apply.
    /// </summary>
    [Fact]
    public void RecordRunCompleted_StampsThePeopleTimeUnderThePeopleLock()
    {
        var task = NewTask();
        var config = new PluginConfiguration();
        var at = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        task.Completed(config, at);

        Assert.Equal(at, config.LastPeopleSyncTime);
        Assert.Null(config.LastMetadataSyncTime);
        Assert.Equal("People", task.MutexKey);
        Assert.Equal("ServerSyncMissingPeople", task.Key);
    }
}
