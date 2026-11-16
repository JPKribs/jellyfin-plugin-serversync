using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.MetadataSync;

/// <summary>
/// A metadata row is built only for an item this server holds inside the Library Filter, and a refresh replaces only the local side of the categories asked for.
/// </summary>
public sealed class MetadataSyncTableServiceTests
{
    private static readonly Guid SourceItemId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid LocalItemId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private readonly ILibraryManager _libraryManager;
    private readonly InterfaceStub _library;
    private readonly ILocalItems _localItems;
    private readonly InterfaceStub _items;
    private readonly ILinkRegistry _linkRegistry;
    private readonly MetadataSyncTableService _service;

    public MetadataSyncTableServiceTests()
    {
        (_libraryManager, _library) = InterfaceStub.Create<ILibraryManager>();
        (_localItems, _items) = InterfaceStub.Create<ILocalItems>();
        (_linkRegistry, _) = InterfaceStub.Create<ILinkRegistry>();
        _service = new MetadataSyncTableService(NullLogger<MetadataSyncTableService>.Instance, _libraryManager, _localItems, _linkRegistry);
    }

    private static Movie LocalMovie() => new() { Id = LocalItemId, Name = "Local Film", Path = "/local/movies/Film/film.mkv", Studios = new[] { "Beta", "alpha" } };

    private static LibraryMapping Library() => new()
    {
        SourceLibraryId = "source-lib",
        LocalLibraryId = "local-lib",
        SourceRootPath = "/media/movies",
        LocalRootPath = "/local/movies"
    };

    private static BaseItemDto SourceItem(string? path = "/media/movies/Film/film.mkv", string? name = "Source Film") => new()
    {
        Id = SourceItemId,
        Path = path,
        Name = name,
        Type = BaseItemDto_Type.Movie
    };

    private static MetadataSyncItem StaleRow(string? localItemId)
    {
        var row = new MetadataSyncItem { SourceItemId = SourceItemId.ToString("N"), LocalItemId = localItemId };
        row.Metadata.UpdateSource("{\"Name\":\"Source Film\"}");
        row.Metadata.Local = "stale-metadata";
        row.Images.Local = "stale-images";
        row.People.Local = "stale-people";
        row.Studios.Local = "stale-studios";
        return row;
    }

    private static SourceServerClient Client(StubHandler handler)
        => new(NullLogger<SourceServerClient>.Instance, new HttpClient(handler), "https://source.example", "key", "test", "0.0.0-test");

    private Task<MetadataSyncItem?> Build(LibraryMapping library, BaseItemDto source, bool isFolder = false, bool metadata = false, bool studios = false, bool people = false)
        => _service.BuildRecordAsync(library, source, isFolder, metadata, syncImages: false, people, studios, syncGenres: true, syncTags: true, priorSourceImagesJson: null, priorLocalImagesJson: null, deepImageVerification: false, client: null!, CancellationToken.None);

    /// <summary>
    /// Each dependency is required.
    /// True: a broken registration fails at startup with the argument named.
    /// False: the service would build and fail later in the middle of a scan.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Constructor_NullDependency_Throws(int missing)
    {
        Assert.Throws<ArgumentNullException>(() => new MetadataSyncTableService(
            missing == 0 ? null! : NullLogger<MetadataSyncTableService>.Instance,
            missing == 1 ? null! : _libraryManager,
            missing == 2 ? null! : _localItems,
            missing == 3 ? null! : _linkRegistry));
    }

    /// <summary>
    /// A row whose local item id is missing or not an id is left alone and nothing is looked up.
    /// True: a row with no local match opens without error.
    /// False: a bad id would throw and the detail view would not open.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-id")]
    public void RefreshLocalSnapshot_LocalIdIsNotAnId_DoesNothing(string? localItemId)
    {
        var row = StaleRow(localItemId);

        _service.RefreshLocalSnapshot(row, true, true, true, true, true, true);

        Assert.Equal("stale-metadata", row.Metadata.Local);
        Assert.Empty(_library.Calls);
    }

    /// <summary>
    /// A local item that is gone leaves the row as it was.
    /// True: the row keeps its last scan's values when the live read finds nothing.
    /// False: the local side would be blanked for an item removed since the scan.
    /// </summary>
    [Fact]
    public void RefreshLocalSnapshot_LocalItemGone_LeavesTheRowAlone()
    {
        var row = StaleRow(LocalItemId.ToString("N"));

        _service.RefreshLocalSnapshot(row, true, true, true, true, true, true);

        Assert.Equal(("stale-metadata", "stale-images", "stale-people", "stale-studios"), (row.Metadata.Local, row.Images.Local, row.People.Local, row.Studios.Local));
    }

    /// <summary>
    /// Only the categories asked for have their local side read again.
    /// True: the detail view follows the Metadata settings, one category at a time.
    /// False: a category that is turned off would be rewritten on every open.
    /// </summary>
    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, false, false)]
    public void RefreshLocalSnapshot_EachSwitch_RefreshesOnlyItsCategory(bool metadata, bool images, bool people, bool studios)
    {
        _library.Answer("GetItemById", _ => LocalMovie());
        var row = StaleRow(LocalItemId.ToString("N"));

        _service.RefreshLocalSnapshot(row, metadata, images, people, studios, syncGenres: true, syncTags: true);

        Assert.Equal(!metadata, row.Metadata.Local == "stale-metadata");
        Assert.Equal(images ? null : "stale-images", row.Images.Local);
        Assert.Equal(people ? "[]" : "stale-people", row.People.Local);
        Assert.Equal(studios ? "[\"alpha\",\"Beta\"]" : "stale-studios", row.Studios.Local);
    }

    /// <summary>
    /// The refreshed metadata is the local item's own, and the source side and its hash are left as they were.
    /// True: the detail view shows live local values against the source values of the last scan.
    /// False: opening a row would change what the next scan compares against.
    /// </summary>
    [Fact]
    public void RefreshLocalSnapshot_Metadata_ReadsTheLocalItemAndLeavesTheSource()
    {
        _library.Answer("GetItemById", args => (Guid)args[0]! == LocalItemId ? LocalMovie() : null);
        var row = StaleRow(LocalItemId.ToString("N"));
        var sourceHash = row.Metadata.SourceHash;

        _service.RefreshLocalSnapshot(row, true, false, false, false, true, true);

        var local = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.Metadata.Local!)!;
        Assert.Equal("Local Film", local["Name"].GetString());
        Assert.Equal("{\"Name\":\"Source Film\"}", row.Metadata.Source);
        Assert.Equal(sourceHash, row.Metadata.SourceHash);
    }

    /// <summary>
    /// A row whose source item id is not an id, or that has no source manifest, calls nothing and changes nothing.
    /// True: a row with nothing to size costs no request.
    /// False: every open would call the source, or a bad id would be sent to it.
    /// </summary>
    [Theory]
    [InlineData("not-an-id", "{\"Primary\":[{\"ImageType\":\"Primary\",\"Tag\":\"t\"}]}")]
    [InlineData("", "{\"Primary\":[{\"ImageType\":\"Primary\",\"Tag\":\"t\"}]}")]
    [InlineData("66666666666666666666666666666666", null)]
    [InlineData("66666666666666666666666666666666", "{}")]
    public async Task EnrichSourceImageSizesAsync_NothingToSize_CallsNothing(string sourceItemId, string? manifest)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = Client(handler);
        var row = new MetadataSyncItem { SourceItemId = sourceItemId };
        row.Images.Source = manifest;

        await _service.EnrichSourceImageSizesAsync(row, client, CancellationToken.None);

        Assert.Equal(0, handler.Requests);
        Assert.Equal(manifest, row.Images.Source);
    }

    /// <summary>
    /// A source that answers with an error leaves the manifest as it was.
    /// True: a source that is down does not change what the row holds.
    /// False: a failed call would blank the manifest and every image would read as changed.
    /// </summary>
    [Fact]
    public async Task EnrichSourceImageSizesAsync_SourceAnswersWithAnError_LeavesTheManifest()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = Client(handler);
        const string Manifest = "{\"Primary\":[{\"ImageType\":\"Primary\",\"ImageIndex\":0,\"Size\":0,\"Width\":0,\"Height\":0,\"Tag\":\"t\"}]}";
        var row = new MetadataSyncItem { SourceItemId = SourceItemId.ToString("N") };
        row.Images.Source = Manifest;

        await _service.EnrichSourceImageSizesAsync(row, client, CancellationToken.None);

        Assert.True(handler.Requests > 0);
        Assert.Equal(Manifest, row.Images.Source);
    }

    /// <summary>
    /// A library id that is not a folder on this server gives two empty lookups.
    /// True: a mapping to a removed library scans as empty.
    /// False: a removed library would throw and stop the scan of every other library.
    /// </summary>
    [Fact]
    public void GetLocalItemsByPath_LibraryIsNotAFolder_ReturnsEmptyLookups()
    {
        _library.Answer("GetItemById", _ => LocalMovie());

        var (leaves, folders) = _service.GetLocalItemsByPath(Guid.NewGuid());

        Assert.Empty(leaves);
        Assert.Empty(folders);
    }

    /// <summary>
    /// A source item with no path builds no row and looks nothing up.
    /// True: a virtual season or artist is not tracked.
    /// False: a pathless item would be matched against the library root.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task BuildRecordAsync_NoSourcePath_ReturnsNull(string? path)
    {
        _items.Answer("FindByPath", _ => LocalMovie());

        var row = await Build(Library(), SourceItem(path));

        Assert.Null(row);
        Assert.Empty(_items.Calls);
    }

    /// <summary>
    /// An item the Library Filter leaves out builds no row, and a sibling that only shares its name's start does.
    /// True: filtered titles have no metadata rows, so the scan prunes any they had.
    /// False: metadata would sync for titles the operator filtered out, or a neighbor would be dropped.
    /// </summary>
    [Theory]
    [InlineData(LibraryFilterMode.Blacklist, "/media/movies/Film", false)]
    [InlineData(LibraryFilterMode.Blacklist, "/media/movies/Fil", true)]
    [InlineData(LibraryFilterMode.Whitelist, "/media/movies/Film", true)]
    [InlineData(LibraryFilterMode.Whitelist, "/media/movies/Film2", false)]
    public async Task BuildRecordAsync_LibraryFilter_DecidesWhetherARowIsBuilt(LibraryFilterMode mode, string filteredPath, bool built)
    {
        _items.Answer("FindByPath", _ => LocalMovie());
        var library = Library();
        library.FilterMode = mode;
        library.FilteredItems = new List<FilteredItem> { new() { Path = filteredPath } };

        var row = await Build(library, SourceItem());

        Assert.Equal(built, row is not null);
    }

    /// <summary>
    /// With no local item at the translated path, no row is built, and the lookup carries the folder flag given.
    /// True: a folder is only matched to a folder and a file to a file.
    /// False: a season folder could be matched to a link file of the same path.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BuildRecordAsync_NoLocalItem_ReturnsNullAfterAskingWithTheFolderFlag(bool isFolder)
    {
        var row = await Build(Library(), SourceItem(), isFolder);

        var call = Assert.Single(_items.Calls);
        Assert.Null(row);
        Assert.Equal("/local/movies/Film/film.mkv", call.Args[0]);
        Assert.Equal(isFolder, call.Args[1]);
    }

    /// <summary>
    /// A local item the caller already holds is used as it is, and the library is asked only for a path the caller does not hold.
    /// True: a scan that listed every local item makes no library query for each row it builds.
    /// False: every row would cost a library query, or an item added during the scan would get no row.
    /// </summary>
    [Fact]
    public async Task BuildRecordAsync_KnownLocalItem_IsUsedWithoutAskingTheLibrary()
    {
        var held = LocalMovie();
        var asked = new List<(string Path, bool IsFolder)>();
        BaseItem? Known(string path, bool isFolder)
        {
            asked.Add((path, isFolder));
            return path == "/local/movies/Film/film.mkv" ? held : null;
        }

        _items.Answer("FindByPath", _ => new Movie { Id = Guid.NewGuid(), Name = "From The Library", Path = "/local/movies/Other/other.mkv" });

        var known = await _service.BuildRecordAsync(Library(), SourceItem(), false, false, false, false, false, true, true, null, null, false, null!, CancellationToken.None, Known);
        var libraryCallsForKnown = _items.Calls.Count(c => c.Name == "FindByPath");
        var unknown = await _service.BuildRecordAsync(Library(), SourceItem("/media/movies/Other/other.mkv"), false, false, false, false, false, true, true, null, null, false, null!, CancellationToken.None, Known);

        Assert.Equal(LocalItemId.ToString("N"), known!.LocalItemId);
        Assert.Equal(0, libraryCallsForKnown);
        Assert.Equal(("/local/movies/Film/film.mkv", false), asked[0]);
        Assert.NotNull(unknown);
        Assert.NotEqual(LocalItemId.ToString("N"), unknown!.LocalItemId);
        Assert.Equal(1, _items.Calls.Count(c => c.Name == "FindByPath"));
    }

    /// <summary>
    /// A cast the last scan read is kept as the source's cast when this scan did not read it, and the local cast is still read from this server.
    /// True: a scan that skips an unchanged cast keeps comparing it with the local cast.
    /// False: a skipped cast reads as empty and every film's cast would be queued for removal.
    /// </summary>
    [Fact]
    public async Task BuildRecordAsync_KeptSourceCast_IsUsedWhenTheCastWasNotRead()
    {
        const string kept = "[{\"Name\":\"Alice Smith\",\"Role\":\"Lead\",\"Type\":\"Actor\"}]";
        _items.Answer("FindByPath", _ => LocalMovie());

        var row = await _service.BuildRecordAsync(Library(), SourceItem(), false, false, false, true, false, false, false, null, null, false, null!, CancellationToken.None, keptSourcePeopleJson: kept);

        Assert.Equal(kept, row!.People.Source);
        Assert.NotNull(row.People.Local);
    }

    /// <summary>
    /// A matched item builds a row with both ids without dashes, the paths, the type, and no blob for a category that is off.
    /// True: the row says which item it is on both servers and holds only what is turned on.
    /// False: the apply step would write to the wrong item, or a category that is off would be compared.
    /// </summary>
    [Fact]
    public async Task BuildRecordAsync_MatchWithEveryCategoryOff_FillsIdentityOnly()
    {
        _items.Answer("FindByPath", _ => LocalMovie());

        var row = (await Build(Library(), SourceItem(), isFolder: true))!;

        Assert.Equal(("source-lib", "local-lib", SourceItemId.ToString("N"), LocalItemId.ToString("N")), (row.SourceLibraryId, row.LocalLibraryId, row.SourceItemId, row.LocalItemId));
        Assert.Equal(("Source Film", "/media/movies/Film/film.mkv", "/local/movies/Film/film.mkv", "Movie", true), (row.ItemName, row.SourcePath, row.LocalPath, row.ItemType, row.IsFolder));
        Assert.Null(row.Metadata.Source);
        Assert.Null(row.Images.Source);
        Assert.Null(row.People.Source);
        Assert.Null(row.Studios.Source);
        Assert.False(row.HasChanges);
    }

    /// <summary>
    /// With metadata and studios on, both sides of each are built and their difference shows as a change.
    /// True: a title named differently on the two servers is queued.
    /// False: the row would hold one side only and never show a change.
    /// </summary>
    [Fact]
    public async Task BuildRecordAsync_MetadataAndStudiosOn_BuildsBothSides()
    {
        _items.Answer("FindByPath", _ => LocalMovie());
        var source = SourceItem();
        source.Studios = new List<NameGuidPair> { new() { Name = "Zeta" } };

        var row = (await Build(Library(), source, metadata: true, studios: true))!;

        var sourceBlob = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.Metadata.Source!)!;
        var localBlob = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.Metadata.Local!)!;
        Assert.Equal(("Source Film", "Local Film"), (sourceBlob["Name"].GetString(), localBlob["Name"].GetString()));
        Assert.Equal(("[\"Zeta\"]", "[\"alpha\",\"Beta\"]"), (row.Studios.Source, row.Studios.Local));
        Assert.True(row.HasMetadataChanges);
        Assert.True(row.HasStudiosChanges);
        Assert.NotNull(row.Metadata.SourceHash);
    }

    /// <summary>
    /// A source item with no name is named after its file, and a mapping with no local library id stores an empty one.
    /// True: every row has something to show and a value the table accepts.
    /// False: a nameless row would list blank, or a null id would fail the write.
    /// </summary>
    [Fact]
    public async Task BuildRecordAsync_NoNameAndNoLocalLibrary_FallsBackToFileNameAndEmptyId()
    {
        _items.Answer("FindByPath", _ => LocalMovie());
        var library = Library();
        library.LocalLibraryId = null!;

        var row = (await Build(library, SourceItem(name: null)))!;

        Assert.Equal("film", row.ItemName);
        Assert.Equal(string.Empty, row.LocalLibraryId);
    }

    /// <summary>
    /// A missing mapping, source item, row, or client is refused.
    /// True: the caller fails at once with the argument named.
    /// False: a scan would fail with a null reference and no cause.
    /// </summary>
    [Fact]
    public async Task Methods_NullArgument_Throw()
    {
        using var client = Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        await Assert.ThrowsAsync<ArgumentNullException>(() => Build(null!, SourceItem()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Build(Library(), null!));
        Assert.Throws<ArgumentNullException>(() => _service.RefreshLocalSnapshot(null!, true, true, true, true, true, true));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _service.EnrichSourceImageSizesAsync(null!, client, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _service.EnrichSourceImageSizesAsync(new MetadataSyncItem(), null!, CancellationToken.None));
    }
}
