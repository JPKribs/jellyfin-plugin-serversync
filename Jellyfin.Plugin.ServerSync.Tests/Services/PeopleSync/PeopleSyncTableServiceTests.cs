using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.PeopleSync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.PeopleSync;

/// <summary>
/// Refreshing a person row replaces only its local side, from the person this server holds, and never touches the source side.
/// </summary>
public sealed class PeopleSyncTableServiceTests
{
    private static readonly Guid PersonId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SourcePersonId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly ILibraryManager _libraryManager;
    private readonly InterfaceStub _library;
    private readonly PeopleSyncTableService _service;

    public PeopleSyncTableServiceTests()
    {
        (_libraryManager, _library) = InterfaceStub.Create<ILibraryManager>();
        _service = new PeopleSyncTableService(NullLogger<PeopleSyncTableService>.Instance, _libraryManager);
    }

    private static PeopleSyncItem Row(string name = "Jane Doe", string? localPersonId = null)
    {
        var row = new PeopleSyncItem { PersonName = name, LocalPersonId = localPersonId, SourcePersonId = SourcePersonId.ToString("N") };
        row.Metadata.UpdateSource("{\"Overview\":\"Source bio\"}");
        row.Metadata.Local = "stale-metadata";
        row.Images.Source = "stale-source-images";
        row.Images.Local = "stale-images";
        return row;
    }

    private static Person LocalPerson() => new() { Id = PersonId, Name = "Jane Doe", Overview = "Local bio" };

    private static SourceServerClient Client(StubHandler handler)
        => new(NullLogger<SourceServerClient>.Instance, new HttpClient(handler), "https://source.example", "key", "test", "0.0.0-test");

    /// <summary>
    /// Each dependency is required.
    /// True: a broken registration fails at startup with the argument named.
    /// False: the service would build and fail later when a row is opened.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Constructor_NullDependency_Throws(bool nullLogger)
    {
        Assert.Throws<ArgumentNullException>(() => new PeopleSyncTableService(
            nullLogger ? null! : NullLogger<PeopleSyncTableService>.Instance,
            nullLogger ? _libraryManager : null!));
    }

    /// <summary>
    /// A row with no person name is left alone and nothing is looked up.
    /// True: a damaged row opens without error.
    /// False: an empty name would be searched for and could match the wrong person.
    /// </summary>
    [Fact]
    public void RefreshLocalSnapshot_NoName_DoesNothing()
    {
        var row = Row(name: string.Empty, localPersonId: PersonId.ToString("N"));

        _service.RefreshLocalSnapshot(row, syncImages: true);

        Assert.Equal("stale-metadata", row.Metadata.Local);
        Assert.Empty(_library.Calls);
    }

    /// <summary>
    /// The stored local id finds the person without a search by name.
    /// True: a person whose name differs only in case is still found.
    /// False: the dashboard would show no local side for a person this server holds.
    /// </summary>
    [Fact]
    public void RefreshLocalSnapshot_StoredIdFindsThePerson_RebuildsTheLocalMetadata()
    {
        _library.Answer("GetItemById", args => (Guid)args[0]! == PersonId ? LocalPerson() : null);
        var row = Row(localPersonId: PersonId.ToString("N"));

        _service.RefreshLocalSnapshot(row, syncImages: false);

        Assert.Contains("\"Overview\":\"Local bio\"", row.Metadata.Local, StringComparison.Ordinal);
        Assert.DoesNotContain(_library.Calls, c => c.Name == "GetPerson");
    }

    /// <summary>
    /// With no stored id, or one that finds nothing, the person is found by name.
    /// True: a row from before the id was recorded still shows its local side.
    /// False: older rows would show an empty local side until the next scan.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("not-an-id")]
    [InlineData("55555555555555555555555555555555")]
    public void RefreshLocalSnapshot_IdMissingOrStale_FallsBackToTheName(string? localPersonId)
    {
        _library.Answer("GetPerson", _ => new Person { Id = PersonId, Name = "Jane Doe" });
        _library.Answer("GetItemById", args => (Guid)args[0]! == PersonId ? LocalPerson() : null);
        var row = Row(localPersonId: localPersonId);

        _service.RefreshLocalSnapshot(row, syncImages: false);

        Assert.Contains("\"Overview\":\"Local bio\"", row.Metadata.Local, StringComparison.Ordinal);
        Assert.Equal("Jane Doe", _library.Calls.Single(c => c.Name == "GetPerson").Args[0]);
    }

    /// <summary>
    /// A person this server does not hold leaves the row as it was.
    /// True: the row keeps its last scan's values when the live read finds nothing.
    /// False: the local side would be blanked, or the page would fail.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefreshLocalSnapshot_PersonNotHeld_LeavesTheRowAlone(bool nameResolvesButItemIsGone)
    {
        if (nameResolvesButItemIsGone)
        {
            _library.Answer("GetPerson", _ => new Person { Id = PersonId, Name = "Jane Doe" });
        }

        var row = Row();

        _service.RefreshLocalSnapshot(row, syncImages: true);

        Assert.Equal("stale-metadata", row.Metadata.Local);
        Assert.Equal("stale-images", row.Images.Local);
    }

    /// <summary>
    /// The local images are read again only when asked, and a person with no images reads as none.
    /// True: the image side follows the People settings.
    /// False: image sync turned off would still rewrite the image side on every open.
    /// </summary>
    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "stale-images")]
    public void RefreshLocalSnapshot_SyncImages_DecidesWhetherImagesAreReRead(bool syncImages, string? expected)
    {
        _library.Answer("GetItemById", _ => LocalPerson());
        var row = Row(localPersonId: PersonId.ToString("N"));

        _service.RefreshLocalSnapshot(row, syncImages);

        Assert.Equal(expected, row.Images.Local);
    }

    /// <summary>
    /// The source value, the source images, and every hash are left as they were.
    /// True: opening a row does not change what the next scan compares against.
    /// False: looking at a row would alter its change detection.
    /// </summary>
    [Fact]
    public void RefreshLocalSnapshot_PersonFound_LeavesTheSourceSideAndHashes()
    {
        _library.Answer("GetItemById", _ => LocalPerson());
        var row = Row(localPersonId: PersonId.ToString("N"));
        var sourceHash = row.Metadata.SourceHash;

        _service.RefreshLocalSnapshot(row, syncImages: true);

        Assert.Equal("{\"Overview\":\"Source bio\"}", row.Metadata.Source);
        Assert.Equal("stale-source-images", row.Images.Source);
        Assert.Equal(sourceHash, row.Metadata.SourceHash);
        Assert.Null(row.Metadata.SyncedHash);
    }

    /// <summary>
    /// A row whose source person id is not an id, or that has no source manifest, calls nothing and changes nothing.
    /// True: a row with nothing to size costs no request.
    /// False: every open would call the source, or a bad id would be sent to it.
    /// </summary>
    [Theory]
    [InlineData("not-an-id", "{\"Primary\":[{\"ImageType\":\"Primary\",\"Tag\":\"t\"}]}")]
    [InlineData(null, "{\"Primary\":[{\"ImageType\":\"Primary\",\"Tag\":\"t\"}]}")]
    [InlineData("44444444444444444444444444444444", null)]
    [InlineData("44444444444444444444444444444444", "")]
    public async Task EnrichSourceImageSizesAsync_NothingToSize_CallsNothing(string? sourcePersonId, string? manifest)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = Client(handler);
        var row = new PeopleSyncItem { PersonName = "Jane Doe", SourcePersonId = sourcePersonId };
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
        var row = new PeopleSyncItem { PersonName = "Jane Doe", SourcePersonId = SourcePersonId.ToString("N") };
        row.Images.Source = Manifest;

        await _service.EnrichSourceImageSizesAsync(row, client, CancellationToken.None);

        Assert.True(handler.Requests > 0);
        Assert.Equal(Manifest, row.Images.Source);
    }

    /// <summary>
    /// A missing row or client is refused.
    /// True: the caller fails at once with the argument named.
    /// False: the page would fail with a null reference and no cause.
    /// </summary>
    [Fact]
    public async Task Methods_NullArgument_Throw()
    {
        using var client = Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        Assert.Throws<ArgumentNullException>(() => _service.RefreshLocalSnapshot(null!, syncImages: true));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _service.EnrichSourceImageSizesAsync(null!, client, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _service.EnrichSourceImageSizesAsync(Row(), null!, CancellationToken.None));
    }
}
