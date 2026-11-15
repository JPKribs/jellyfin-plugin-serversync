using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tasks.ContentSync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Tasks.ContentSync;

/// <summary>
/// The collections step mirrors each whitelisted source collection onto the local copies of its members, and leaves a mirror alone whenever the source cannot be read.
/// </summary>
public sealed class SyncCollectionsTaskTests
{
    private static readonly Guid CollectionId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherCollectionId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid LocalMovieId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid CreatedId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    private readonly FakeConfigManager _config = new();
    private readonly TaskClientFactory _factory = new();
    private readonly List<BaseItem> _localBoxSets = new();
    private readonly List<CollectionCreationOptions> _created = new();
    private readonly List<(Guid Collection, Guid[] Items)> _added = new();
    private readonly List<(Guid Collection, Guid[] Items)> _removed = new();
    private readonly List<BaseItem> _updated = new();
    private readonly InterfaceStub _libraryStub;
    private readonly ILibraryManager _library;
    private readonly ICollectionManager _collections;
    private readonly ILocalItems _localItems;
    private bool _localMovieExists = true;

    public SyncCollectionsTaskTests()
    {
        _config.Configuration.EnableContentSync = true;
        _config.Configuration.MirrorSyncedCollections = true;

        (_library, _libraryStub) = InterfaceStub.Create<ILibraryManager>();
        _libraryStub.Answer(nameof(ILibraryManager.GetItemList), _ => _localBoxSets);
        _libraryStub.Answer(nameof(ILibraryManager.UpdateItemAsync), args =>
        {
            _updated.Add((BaseItem)args[0]!);
            return System.Threading.Tasks.Task.CompletedTask;
        });

        (_collections, var collectionStub) = InterfaceStub.Create<ICollectionManager>();
        collectionStub.Answer(nameof(ICollectionManager.CreateCollectionAsync), args =>
        {
            _created.Add((CollectionCreationOptions)args[0]!);
            return System.Threading.Tasks.Task.FromResult(new BoxSet { Id = CreatedId });
        });
        collectionStub.Answer(nameof(ICollectionManager.AddToCollectionAsync), args =>
        {
            _added.Add(((Guid)args[0]!, ((IEnumerable<Guid>)args[1]!).ToArray()));
            return System.Threading.Tasks.Task.CompletedTask;
        });
        collectionStub.Answer(nameof(ICollectionManager.RemoveFromCollectionAsync), args =>
        {
            _removed.Add(((Guid)args[0]!, ((IEnumerable<Guid>)args[1]!).ToArray()));
            return System.Threading.Tasks.Task.CompletedTask;
        });

        (_localItems, var localStub) = InterfaceStub.Create<ILocalItems>();
        localStub.Answer(nameof(ILocalItems.FindByPath), args =>
            _localMovieExists && ((string)args[0]!).Replace('\\', '/').EndsWith("/local/movies/A.mkv", StringComparison.Ordinal)
                ? new Movie { Id = LocalMovieId }
                : null);
    }

    private SyncCollectionsTask Task() => new(NullLogger<SyncCollectionsTask>.Instance, _config, _factory, _library, _collections, _localItems);

    private SourceServer AddServer(string key, params Guid[] whitelistedCollections)
    {
        var mapping = new LibraryMapping
        {
            SourceLibraryId = "lib-1",
            SourceRootPath = "/src/movies",
            LocalRootPath = "/local/movies",
            FilterMode = LibraryFilterMode.Whitelist
        };
        foreach (var id in whitelistedCollections)
        {
            mapping.FilteredItems.Add(new FilteredItem { ItemId = id.ToString("N"), Type = "BoxSet", Name = "Favorites" });
        }

        var server = new SourceServer { Key = key, Name = key, Url = "http://" + key + ":8096", ApiKey = "k" };
        server.LibraryMappings.Add(mapping);
        _config.Configuration.Servers.Add(server);
        return server;
    }

    /// <summary>
    /// Serves the source's item route: a collection by id, and its one member, a film under the mapped root.
    /// </summary>
    private void ServeCollection(string serverKey, bool collectionExists = true, string name = "Favorites")
    {
        _factory.HandlerFor(serverKey).On("/Items", request =>
        {
            var query = request.RequestUri!.Query;
            if (query.Contains("parentId", StringComparison.OrdinalIgnoreCase))
            {
                return TaskSourceHandler.Json("{\"Items\":[{\"Id\":\"dddddddd-0000-0000-0000-000000000001\",\"Name\":\"A\",\"Type\":\"Movie\",\"Path\":\"/src/movies/A.mkv\"}],\"TotalRecordCount\":1}");
            }

            if (!collectionExists || !query.Contains(CollectionId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return TaskSourceHandler.Json("{\"Items\":[],\"TotalRecordCount\":0}");
            }

            return TaskSourceHandler.Json("{\"Items\":[{\"Id\":\"" + CollectionId + "\",\"Name\":\"" + name + "\",\"Type\":\"BoxSet\"}],\"TotalRecordCount\":1}");
        });
    }

    private int LibraryReads => _libraryStub.Calls.Count(c => c.Name == nameof(ILibraryManager.GetItemList));

    /// <summary>
    /// A whitelisted collection with a local member and no local mirror is created locked, tagged with the source id, and filled with the local copy.
    /// True: the collection appears here holding this server's copies, and later runs find it by its tag.
    /// False: no collection appears, or it holds source ids that mean nothing here.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NoLocalMirror_CreatesATaggedCollectionWithTheLocalCopies()
    {
        AddServer("a", CollectionId);
        ServeCollection("a");

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        var options = Assert.Single(_created);
        Assert.Equal("Favorites", options.Name);
        Assert.True(options.IsLocked);
        Assert.Equal(CollectionId.ToString("N"), options.ProviderIds[SyncCollectionsTask.SourceCollectionProviderKey]);
        var added = Assert.Single(_added);
        Assert.Equal(CreatedId, added.Collection);
        Assert.Equal(new[] { LocalMovieId }, added.Items);
    }

    /// <summary>
    /// A collection none of whose members exists here yet is not created.
    /// True: an empty collection does not appear before content has been downloaded.
    /// False: empty collections clutter the library, and Jellyfin may remove them again.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NoMemberHereYet_CreatesNothing()
    {
        AddServer("a", CollectionId);
        ServeCollection("a");
        _localMovieExists = false;

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.Empty(_created);
        Assert.Empty(_added);
    }

    /// <summary>
    /// A local collection already tagged with the source id is updated in place, even under another name.
    /// True: a rename on the source keeps one mirror and adds the missing member to it.
    /// False: every rename creates a second collection.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TaggedMirrorUnderAnotherName_IsUpdatedNotDuplicated()
    {
        AddServer("a", CollectionId);
        ServeCollection("a", name: "Renamed");
        var mirror = new BoxSet { Id = Guid.NewGuid(), Name = "Favorites" };
        mirror.ProviderIds[SyncCollectionsTask.SourceCollectionProviderKey] = CollectionId.ToString("N");
        _localBoxSets.Add(mirror);

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.Empty(_created);
        var added = Assert.Single(_added);
        Assert.Equal(mirror.Id, added.Collection);
        Assert.Equal(new[] { LocalMovieId }, added.Items);
        Assert.Empty(_updated);
    }

    /// <summary>
    /// An empty local collection of the same name is adopted: it is tagged, saved, and filled.
    /// True: a collection the administrator made ahead of time becomes the mirror.
    /// False: a second collection of the same name is created beside it.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_EmptyCollectionOfTheSameName_IsAdoptedAndTagged()
    {
        AddServer("a", CollectionId);
        ServeCollection("a");
        var empty = new BoxSet { Id = Guid.NewGuid(), Name = "favorites" };
        _localBoxSets.Add(empty);

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.Empty(_created);
        Assert.Equal(CollectionId.ToString("N"), empty.ProviderIds[SyncCollectionsTask.SourceCollectionProviderKey]);
        Assert.Same(empty, Assert.Single(_updated));
        Assert.Equal(empty.Id, Assert.Single(_added).Collection);
    }

    /// <summary>
    /// A source collection that no longer exists changes nothing here, and the next collection is still mirrored.
    /// True: a mirror is never emptied because its source vanished, and one dead entry does not stop the rest.
    /// False: a deleted or unreadable source collection wipes the local one, or blocks every other collection.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SourceCollectionGone_LeavesItAndMirrorsTheRest()
    {
        AddServer("a", OtherCollectionId, CollectionId);
        ServeCollection("a");
        var progress = new TaskProgress();

        await Task().ExecuteAsync(progress, CancellationToken.None);

        Assert.Single(_created);
        Assert.Empty(_removed);
        Assert.Equal(new[] { 50.0, 100.0, 100.0 }, progress.Values);
    }

    /// <summary>
    /// A server that answers the connection test with an error is skipped, its collections count as handled, and nothing changes here.
    /// True: a source being down leaves every mirror as it is and the bar still finishes.
    /// False: an unreachable source empties its mirrors, or the step fails Sync Content.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ServerAnswersWithError_SkipsItAndChangesNothing()
    {
        AddServer("a", CollectionId);
        _factory.HandlerFor("a").SystemInfoStatus = HttpStatusCode.InternalServerError;
        var progress = new TaskProgress();

        await Task().ExecuteAsync(progress, CancellationToken.None);

        Assert.Empty(_created);
        Assert.Empty(_added);
        Assert.Equal(0, _factory.HandlerFor("a").CountOf("/Items"));
        Assert.Equal(new[] { 100.0, 100.0 }, progress.Values);
    }

    /// <summary>
    /// A server whose client cannot be built is skipped, and the next server's collection is still mirrored.
    /// True: one bad Server URL does not stop the other servers' collections.
    /// False: a mistyped URL on one entry ends the whole step.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ClientRejected_GoesOnToTheNextServer()
    {
        AddServer("bad", CollectionId);
        AddServer("good", CollectionId);
        _factory.Rejected.Add("bad");
        ServeCollection("good");

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.Equal(new[] { "bad", "good" }, _factory.Created);
        Assert.Single(_created);
    }

    /// <summary>
    /// Content being off, or mirroring being off, ends the step before the library or any server is read.
    /// True: the setting turns collection mirroring off completely.
    /// False: collections keep being created after the administrator turned mirroring off.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ExecuteAsync_SwitchedOff_ReadsNothing(bool contentSync, bool mirror)
    {
        AddServer("a", CollectionId);
        _config.Configuration.EnableContentSync = contentSync;
        _config.Configuration.MirrorSyncedCollections = mirror;
        var progress = new TaskProgress();

        await Task().ExecuteAsync(progress, CancellationToken.None);

        Assert.Empty(_factory.Created);
        Assert.Equal(0, LibraryReads);
        Assert.Empty(progress.Values);
    }

    /// <summary>
    /// Only whitelisted collection entries of an enabled mapping count, so a blacklist, a whitelisted series, a bad id, and a disabled mapping give the step nothing to do.
    /// True: a collection is mirrored only when the administrator whitelisted that collection.
    /// False: blacklisted collections are mirrored, or a series id is fetched as a collection.
    /// </summary>
    [Theory]
    [InlineData("blacklist")]
    [InlineData("series")]
    [InlineData("badid")]
    [InlineData("disabled")]
    [InlineData("empty")]
    public async Task ExecuteAsync_NothingWhitelistedAsACollection_AsksNoServer(string shape)
    {
        var mapping = AddServer("a", CollectionId).LibraryMappings[0];
        switch (shape)
        {
            case "blacklist":
                mapping.FilterMode = LibraryFilterMode.Blacklist;
                break;
            case "series":
                mapping.FilteredItems[0].Type = "Series";
                break;
            case "badid":
                mapping.FilteredItems[0].ItemId = "not-a-guid";
                break;
            case "disabled":
                mapping.IsEnabled = false;
                break;
            default:
                mapping.FilteredItems.Clear();
                break;
        }

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.Empty(_factory.Created);
        Assert.Equal(0, LibraryReads);
    }

    /// <summary>
    /// A cancelled token stops the step before any server is asked.
    /// True: stopping Sync Content ends this step as cancelled.
    /// False: a stopped task goes on creating collections.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CancelledToken_Throws()
    {
        AddServer("a", CollectionId);
        ServeCollection("a");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task().ExecuteAsync(new TaskProgress(), cts.Token));

        Assert.Empty(_factory.Created);
        Assert.Empty(_created);
    }

    /// <summary>
    /// The step is hidden, has no schedule of its own, and keeps its stored key.
    /// True: it runs only as the last step of Sync Content.
    /// False: it also runs on a timer of its own, beside the scan that feeds it.
    /// </summary>
    [Fact]
    public void Properties_HideTheStepAndGiveItNoSchedule()
    {
        var task = Task();

        Assert.True(task.IsHidden);
        Assert.True(task.IsEnabled);
        Assert.True(task.IsLogged);
        Assert.Empty(task.GetDefaultTriggers());
        Assert.Equal("ServerSyncCollections", task.Key);
    }

    /// <summary>
    /// A missing progress reporter is refused.
    /// True: the mistake is named at the call.
    /// False: the step fails after it has created collections.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NullProgress_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Task().ExecuteAsync(null!, CancellationToken.None));
    }

    /// <summary>
    /// Each missing dependency is refused by name.
    /// True: a wiring mistake fails when the step is built.
    /// False: the step fails part way through a Sync Content run.
    /// </summary>
    [Theory]
    [InlineData("logger")]
    [InlineData("configManager")]
    [InlineData("clientFactory")]
    [InlineData("libraryManager")]
    [InlineData("collectionManager")]
    [InlineData("localItems")]
    public void Constructor_NullArgument_Throws(string missing)
    {
        var thrown = Assert.Throws<ArgumentNullException>(() => new SyncCollectionsTask(
            missing == "logger" ? null! : NullLogger<SyncCollectionsTask>.Instance,
            missing == "configManager" ? null! : _config,
            missing == "clientFactory" ? null! : _factory,
            missing == "libraryManager" ? null! : _library,
            missing == "collectionManager" ? null! : _collections,
            missing == "localItems" ? null! : _localItems));

        Assert.Equal(missing, thrown.ParamName);
    }
}
