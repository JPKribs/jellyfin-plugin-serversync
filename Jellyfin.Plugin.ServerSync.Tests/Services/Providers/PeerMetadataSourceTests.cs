using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Providers;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Providers;

/// <summary>
/// The metadata source must find an item on the sources in rank order, honor a pin, never let one failing source hide the rest, and fetch images only from a source's own address.
/// </summary>
public sealed class PeerMetadataSourceTests
{
    private const string LocalFilm = ProviderTestHarness.LocalRoot + "/Film (2020)/film.mkv";
    private static readonly Guid ItemOnA = Guid.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly Guid ItemOnB = Guid.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

    private readonly ProviderTestHarness _harness = new();

    /// <summary>
    /// The constructor refuses each missing dependency.
    /// True: a broken registration fails when Jellyfin builds its services.
    /// False: the first metadata refresh would fail with a null reference.
    /// </summary>
    [Fact]
    public void Constructor_MissingDependency_Throws()
    {
        var library = InterfaceStub.Create<ILibraryManager>().Proxy;
        var logger = NullLogger<PeerMetadataSource>.Instance;

        Assert.Throws<ArgumentNullException>(() => new PeerMetadataSource(null!, _harness.Factory, library, _harness.Config, logger));
        Assert.Throws<ArgumentNullException>(() => new PeerMetadataSource(_harness.Directory, null!, library, _harness.Config, logger));
        Assert.Throws<ArgumentNullException>(() => new PeerMetadataSource(_harness.Directory, _harness.Factory, null!, _harness.Config, logger));
        Assert.Throws<ArgumentNullException>(() => new PeerMetadataSource(_harness.Directory, _harness.Factory, library, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new PeerMetadataSource(_harness.Directory, _harness.Factory, library, _harness.Config, null!));
    }

    /// <summary>
    /// A pin is the server entry's key, a colon, and the item's id without dashes.
    /// True: a pinned item reads back to the same server and item.
    /// False: a pin written in another shape would never be read, and the pin would be lost.
    /// </summary>
    [Fact]
    public void PinValue_ServerAndItem_IsKeyColonId()
    {
        var value = PeerMetadataSource.PinValue(new SourceServer { Key = "srv-1" }, ItemOnA);

        Assert.Equal("srv-1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", value);
        Assert.Throws<ArgumentNullException>(() => PeerMetadataSource.PinValue(null!, ItemOnA));
    }

    /// <summary>
    /// With no source that answers, nothing is found.
    /// True: a server with no sources fills nothing and fails nothing.
    /// False: every refresh on such a server would throw.
    /// </summary>
    [Fact]
    public async Task FindAllAsync_NoSources_FindsNothing()
    {
        var matches = await _harness.Source.FindAllAsync(Keys(), firstOnly: false, CancellationToken.None);

        Assert.Empty(matches);
        await Assert.ThrowsAsync<ArgumentNullException>(() => _harness.Source.FindAllAsync(null!, false, CancellationToken.None));
    }

    /// <summary>
    /// The lookup sent to a source carries the item's path as that source knows it.
    /// True: the source matches the exact file by its own path.
    /// False: the source would be asked for a path that only exists on this server.
    /// </summary>
    [Fact]
    public async Task FindAllAsync_LocalPath_IsAskedForAsTheSourcesPath()
    {
        var server = _harness.AddSource("a").OnJson("/Sync/Peer/Lookup", ProviderTestHarness.FoundItem(ItemOnA));

        var matches = await _harness.Source.FindAllAsync(Keys(), firstOnly: false, CancellationToken.None);

        using var match = Assert.Single(matches);
        Assert.Equal(ItemOnA, match.ItemId);
        Assert.Equal("a", match.Parent.Key);
        var body = Assert.Single(server.To("/Sync/Peer/Lookup")).Body;
        Assert.Contains("\"Path\":\"" + ProviderTestHarness.SourceRoot + "/Film (2020)/film.mkv\"", body, StringComparison.Ordinal);
        Assert.Contains("\"ItemType\":\"Movie\"", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The provider asks no server for an item unless Enable metadata sync and its provider switch are both on, and for a person unless Enable people sync and its provider switch are both on.
    /// True: a server that turned the provider off, or does not sync that type, makes no request and logs nothing.
    /// False: every library refresh asks the sources, and logs that no source answered.
    /// </summary>
    [Theory]
    [InlineData("Movie", true, true, true, true, true)]
    [InlineData("Movie", false, true, true, true, false)]
    [InlineData("Movie", true, false, true, true, false)]
    [InlineData("Movie", true, true, false, false, true)]
    [InlineData("Person", true, true, true, true, true)]
    [InlineData("Person", true, true, false, true, false)]
    [InlineData("Person", true, true, true, false, false)]
    [InlineData("Person", false, false, true, true, true)]
    public async Task FindAllAsync_ProviderSwitches_DecideWhetherAnyServerIsAsked(string itemType, bool metadataSync, bool metadataProvider, bool peopleSync, bool peopleProvider, bool asks)
    {
        var server = _harness.AddSource("a").OnJson("/Sync/Peer/Lookup", ProviderTestHarness.FoundItem(ItemOnA));
        var config = _harness.Config.Configuration;
        config.EnableMetadataSync = metadataSync;
        config.MetadataSyncProvider = metadataProvider;
        config.EnablePeopleSync = peopleSync;
        config.PeopleSyncProvider = peopleProvider;

        var matches = await _harness.Source.FindAllAsync(Keys() with { ItemType = itemType }, firstOnly: false, CancellationToken.None);
        var found = await _harness.Source.FindAsync(Keys() with { ItemType = itemType }, CancellationToken.None);

        foreach (var match in matches)
        {
            match.Dispose();
        }

        found?.Dispose();
        Assert.Equal(asks, matches.Count == 1);
        Assert.Equal(asks, found is not null);
        Assert.Equal(asks, server.Seen.Count > 0);
    }

    /// <summary>
    /// An image is not fetched while the provider is off for items and for people.
    /// True: a server with the provider off never calls a source, even for an address Jellyfin kept from earlier.
    /// False: an image refresh still reaches a source after the provider was turned off.
    /// </summary>
    [Fact]
    public async Task GetImageResponseAsync_ProviderOffForBoth_Refuses()
    {
        _harness.Config.Configuration.MetadataSyncProvider = false;
        _harness.Config.Configuration.PeopleSyncProvider = false;

        await Assert.ThrowsAsync<HttpRequestException>(() => _harness.Source.GetImageResponseAsync("http://a:8096/Items/x/Images/Primary/0", CancellationToken.None));
    }

    /// <summary>
    /// Asked for the first match only, the search stops at the first source that has the item.
    /// True: the highest ranked source speaks for the item and lower ones are not called.
    /// False: every refresh would call every source, and a lower ranked one could answer.
    /// </summary>
    [Fact]
    public async Task FindAllAsync_FirstOnly_StopsAtTheFirstSourceThatHasIt()
    {
        _harness.AddSource("a").OnJson("/Sync/Peer/Lookup", ProviderTestHarness.FoundItem(ItemOnA));
        var second = _harness.AddSource("b").OnJson("/Sync/Peer/Lookup", ProviderTestHarness.FoundItem(ItemOnB));

        var matches = await _harness.Source.FindAllAsync(Keys(), firstOnly: true, CancellationToken.None);

        using var match = Assert.Single(matches);
        Assert.Equal("a", match.Parent.Key);
        Assert.Empty(second.To("/Sync/Peer/Lookup"));
    }

    /// <summary>
    /// Asked for all, every source that has the item is returned in rank order, and one that lacks it or names no usable id is skipped.
    /// True: the search dialog offers each server's copy of the item.
    /// False: an answer with a broken id would be offered and fail when picked.
    /// </summary>
    [Fact]
    public async Task FindAllAsync_All_ReturnsEverySourceThatHasIt()
    {
        _harness.AddSource("a").OnJson("/Sync/Peer/Lookup", ProviderTestHarness.FoundItem(ItemOnA));
        _harness.AddSource("none").OnJson("/Sync/Peer/Lookup", "{\"Found\":false}");
        _harness.AddSource("broken").OnJson("/Sync/Peer/Lookup", "{\"Found\":true,\"ItemId\":\"not-a-guid\"}");
        _harness.AddSource("b").OnJson("/Sync/Peer/Lookup", ProviderTestHarness.FoundItem(ItemOnB));

        var matches = await _harness.Source.FindAllAsync(Keys(), firstOnly: false, CancellationToken.None);

        Assert.Equal(new[] { ("a", ItemOnA), ("b", ItemOnB) }, matches.Select(m => (m.Parent.Key, m.ItemId)));
        matches.ForEach(m => m.Dispose());
    }

    /// <summary>
    /// A source that refuses or cannot be reached does not stop the search.
    /// True: the next source still fills the item.
    /// False: one source that is down would leave every item without metadata.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindAllAsync_OneSourceFails_TheOthersStillAnswer(bool refuses)
    {
        var failing = _harness.AddSource("a");
        _ = refuses ? failing.OnStatus("/Sync/Peer/Lookup", HttpStatusCode.Forbidden, "no") : failing.OnThrow("/Sync/Peer/Lookup", new HttpRequestException("reset"));
        _harness.AddSource("b").OnJson("/Sync/Peer/Lookup", ProviderTestHarness.FoundItem(ItemOnB));

        var matches = await _harness.Source.FindAllAsync(Keys(), firstOnly: true, CancellationToken.None);

        using var match = Assert.Single(matches);
        Assert.Equal("b", match.Parent.Key);
    }

    /// <summary>
    /// An item outside every mapped library, with no ids to match by, is not asked for.
    /// True: an item a source cannot possibly identify costs no request.
    /// False: the source would be asked a question with nothing in it.
    /// </summary>
    [Fact]
    public async Task FindAllAsync_NothingToMatchBy_AsksNoSource()
    {
        var server = _harness.AddSource("a").OnJson("/Sync/Peer/Lookup", ProviderTestHarness.FoundItem(ItemOnA));

        var matches = await _harness.Source.FindAllAsync(Keys("/unmapped/film.mkv"), firstOnly: false, CancellationToken.None);

        Assert.Empty(matches);
        Assert.Empty(server.To("/Sync/Peer/Lookup"));
    }

    /// <summary>
    /// A cancelled search throws the cancellation.
    /// True: a cancelled refresh ends at once.
    /// False: a cancelled lookup would be counted as a source that could not be asked, and the search would go on.
    /// </summary>
    [Fact]
    public async Task FindAllAsync_Cancelled_Throws()
    {
        var first = _harness.AddSource("a").OnJson("/Sync/Peer/Lookup", ProviderTestHarness.FoundItem(ItemOnA));
        var second = _harness.AddSource("b").OnJson("/Sync/Peer/Lookup", ProviderTestHarness.FoundItem(ItemOnB));
        await _harness.Directory.ParentsAsync(CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _harness.Source.FindAllAsync(Keys(), firstOnly: false, cancelled.Token));

        Assert.Empty(first.To("/Sync/Peer/Lookup"));
        Assert.Empty(second.To("/Sync/Peer/Lookup"));
    }

    /// <summary>
    /// A pinned item goes straight to its pinned source and item, with no lookup.
    /// True: an item the operator matched by hand stays matched to that server's item.
    /// False: the next refresh would search again and could pick another server's item.
    /// </summary>
    [Fact]
    public async Task FindAsync_PinnedToAListedSource_UsesThePinWithoutALookup()
    {
        var first = _harness.AddSource("a").OnJson("/Sync/Peer/Lookup", ProviderTestHarness.FoundItem(ItemOnA));
        var pinned = _harness.AddSource("b");
        var keys = Keys(ids: new() { [PeerMetadataSource.PinnedProviderKey] = "B:" + ItemOnB.ToString("N") });

        using var match = await _harness.Source.FindAsync(keys, CancellationToken.None);

        Assert.Equal("b", match!.Parent.Key);
        Assert.Equal(ItemOnB, match.ItemId);
        Assert.Empty(first.To("/Sync/Peer/Lookup"));
        Assert.Empty(pinned.To("/Sync/Peer/Lookup"));
    }

    /// <summary>
    /// A pin to a server that is not listed, or a pin that cannot be read, falls back to a search.
    /// True: an item pinned to a removed server is still filled from another.
    /// False: a stale pin would leave the item with no metadata forever.
    /// </summary>
    [Theory]
    [InlineData("gone:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("a:not-a-guid")]
    [InlineData(":aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("no-colon")]
    [InlineData(" ")]
    public async Task FindAsync_PinThatCannotBeUsed_FallsBackToASearch(string pin)
    {
        var server = _harness.AddSource("a").OnJson("/Sync/Peer/Lookup", ProviderTestHarness.FoundItem(ItemOnA));
        var keys = Keys(ids: new() { [PeerMetadataSource.PinnedProviderKey] = pin });

        using var match = await _harness.Source.FindAsync(keys, CancellationToken.None);

        Assert.Equal(ItemOnA, match!.ItemId);
        Assert.Single(server.To("/Sync/Peer/Lookup"));
    }

    /// <summary>
    /// An item no source has is not found.
    /// True: the provider reports no metadata and Jellyfin moves to the next provider.
    /// False: a missing item would throw and fail the refresh.
    /// </summary>
    [Fact]
    public async Task FindAsync_NoSourceHasIt_IsNull()
    {
        _harness.AddSource("a").OnJson("/Sync/Peer/Lookup", "{\"Found\":false}");

        Assert.Null(await _harness.Source.FindAsync(Keys(), CancellationToken.None));
    }

    /// <summary>
    /// Each source that has the item becomes one search result, named for its server and carrying a pin to it.
    /// True: picking a result in Identify pins the item to that server.
    /// False: the operator could not tell the servers apart, or the pick would not stick.
    /// </summary>
    [Fact]
    public async Task GetSearchResultsAsync_SourcesThatHaveIt_BecomeNamedPinnedResults()
    {
        _harness.AddSource("a", "Attic")
            .OnJson("/Sync/Peer/Lookup", ProviderTestHarness.FoundItem(ItemOnA))
            .OnJson("/Items", "{\"Items\":[{\"Id\":\"" + ItemOnA.ToString("N") + "\",\"Name\":\"Film\",\"ProductionYear\":2020,\"Overview\":\"About it\",\"IndexNumber\":3,\"ProviderIds\":{\"Imdb\":\"tt1\",\"Blank\":\" \"}}]}");
        _harness.AddSource("b", "Basement")
            .OnJson("/Sync/Peer/Lookup", ProviderTestHarness.FoundItem(ItemOnB))
            .OnStatus("/Items", HttpStatusCode.InternalServerError);

        var results = (await _harness.Source.GetSearchResultsAsync("Movie", new MovieInfo { Path = LocalFilm, Name = "Film" }, CancellationToken.None)).ToList();

        var result = Assert.Single(results);
        Assert.Equal("Film on Attic", result.Name);
        Assert.Equal("Server Sync (Attic)", result.SearchProviderName);
        Assert.Equal(2020, result.ProductionYear);
        Assert.Equal("About it", result.Overview);
        Assert.Equal(3, result.IndexNumber);
        Assert.Equal("https://a.example/Items/" + ItemOnA.ToString("N") + "/Images/Primary?maxHeight=400", result.ImageUrl);
        Assert.Equal("tt1", result.ProviderIds["imdb"]);
        Assert.Equal("a:" + ItemOnA.ToString("N"), result.ProviderIds[PeerMetadataSource.PinnedProviderKey]);
        Assert.False(result.ProviderIds.ContainsKey("Blank"));
    }

    /// <summary>
    /// Every client opened for a search is disposed once its result is read.
    /// True: a search leaves no client behind.
    /// False: every Identify search would leak a client per source.
    /// </summary>
    [Fact]
    public async Task GetSearchResultsAsync_AfterTheSearch_EveryClientIsDisposed()
    {
        _harness.AddSource("a")
            .OnJson("/Sync/Peer/Lookup", ProviderTestHarness.FoundItem(ItemOnA))
            .OnJson("/Items", ClientJson.Page(1, ClientJson.Item(ItemOnA, name: "Film")));
        _harness.AddSource("b").OnJson("/Sync/Peer/Lookup", "{\"Found\":false}");

        await _harness.Source.GetSearchResultsAsync("Movie", new MovieInfo { Path = LocalFilm }, CancellationToken.None);

        Assert.NotEmpty(_harness.Factory.Clients);
        foreach (var client in _harness.Factory.Clients)
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.FindItemDetailsAsync(ItemOnA));
        }
    }

    /// <summary>
    /// An item no source has, or whose read fails, answers with no metadata under this provider's name.
    /// True: Jellyfin moves on to its other providers.
    /// False: a source that is down would fail the item's whole refresh.
    /// </summary>
    [Theory]
    [InlineData("not-found")]
    [InlineData("read-fails")]
    [InlineData("read-empty")]
    public async Task GetMetadataAsync_NothingToRead_HasNoMetadata(string scenario)
    {
        var server = _harness.AddSource("a").OnJson("/Sync/Peer/Lookup", scenario == "not-found" ? "{\"Found\":false}" : ProviderTestHarness.FoundItem(ItemOnA));
        _ = scenario == "read-fails" ? server.OnStatus("/Items", HttpStatusCode.InternalServerError) : server.OnJson("/Items", ClientJson.Page(0));

        var result = await _harness.Source.GetMetadataAsync<Movie>(new MovieInfo { Path = LocalFilm, Name = "Film" }, CancellationToken.None);

        Assert.False(result.HasMetadata);
        Assert.Null(result.Item);
        Assert.Equal("Server Sync", result.Provider);
    }

    /// <summary>
    /// A found item is filled from the source's values, and is pinned only when it already was.
    /// True: an automatic match can move to a better source later, and a hand picked one stays.
    /// False: every automatic match would pin itself to whichever source answered first.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetMetadataAsync_FoundItem_IsFilledAndKeepsOnlyAnExistingPin(bool pinned)
    {
        var server = _harness.AddSource("a")
            .OnJson("/Sync/Peer/Lookup", ProviderTestHarness.FoundItem(ItemOnA))
            .OnJson("/Items", "{\"Items\":[{\"Id\":\"" + ItemOnA.ToString("N") + "\",\"Type\":\"Movie\",\"Name\":\"Film\",\"Overview\":\"About it\",\"PreferredMetadataLanguage\":\"de\",\"Studios\":[{\"Name\":\"Acme\"}]}]}");
        var info = new MovieInfo { Path = LocalFilm, Name = "film" };
        if (pinned)
        {
            info.ProviderIds[PeerMetadataSource.PinnedProviderKey] = "a:" + ItemOnA.ToString("N");
        }

        var result = await _harness.Source.GetMetadataAsync<Movie>(info, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.Equal("Film", result.Item.Name);
        Assert.Equal("About it", result.Item.Overview);
        Assert.Equal(new[] { "Acme" }, result.Item.Studios);
        Assert.Equal("de", result.ResultLanguage);
        Assert.Equal(pinned, result.Item.ProviderIds.ContainsKey(PeerMetadataSource.PinnedProviderKey));
        Assert.Equal(pinned ? 0 : 1, server.To("/Sync/Peer/Lookup").Count);
        Assert.Contains("ids=" + ItemOnA, Assert.Single(server.To("/Items")).Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each image a source lists is offered with a fetch address on the Server URL and a thumbnail on the External URL.
    /// True: Jellyfin downloads through the address it can reach, and the browser shows thumbnails through the one it can.
    /// False: thumbnails would point at an internal address the browser cannot open.
    /// </summary>
    [Fact]
    public async Task GetImagesAsync_ListedImages_AreOfferedWithFetchAndThumbnailAddresses()
    {
        _harness.AddSource("a", "Attic")
            .OnJson("/Sync/Peer/Lookup", ProviderTestHarness.FoundItem(ItemOnA))
            .OnJson("/Items/" + ItemOnA + "/Images", "[{\"ImageType\":\"Primary\",\"Width\":100,\"Height\":150},{\"ImageType\":\"Backdrop\",\"ImageIndex\":2},{\"Width\":5}]");
        _harness.Entry("a").ExternalUrl = "https://public.example/";

        var images = (await _harness.Source.GetImagesAsync(new Movie { Path = LocalFilm, Name = "Film" }, CancellationToken.None)).ToList();

        Assert.Equal(2, images.Count);
        Assert.Equal(ImageType.Primary, images[0].Type);
        Assert.Equal("Server Sync (Attic)", images[0].ProviderName);
        Assert.Equal((100, 150), (images[0].Width, images[0].Height));
        Assert.Equal("https://a.example/Items/" + ItemOnA.ToString("N") + "/Images/Primary/0", images[0].Url);
        Assert.Equal("https://public.example/Items/" + ItemOnA.ToString("N") + "/Images/Primary/0?maxHeight=400", images[0].ThumbnailUrl);
        Assert.Equal("https://a.example/Items/" + ItemOnA.ToString("N") + "/Images/Backdrop/2", images[1].Url);
    }

    /// <summary>
    /// The pinned source's images come first, and a source whose images cannot be listed is skipped.
    /// True: the server the item is matched to supplies its artwork ahead of the others.
    /// False: a higher ranked server's poster would replace the pinned one's, or one failing source would hide every image.
    /// </summary>
    [Fact]
    public async Task GetImagesAsync_PinnedSource_ComesFirst()
    {
        _harness.AddSource("a", "Attic")
            .OnJson("/Sync/Peer/Lookup", ProviderTestHarness.FoundItem(ItemOnA))
            .OnJson("/Items/" + ItemOnA + "/Images", "[{\"ImageType\":\"Primary\"}]");
        _harness.AddSource("down", "Down")
            .OnJson("/Sync/Peer/Lookup", ProviderTestHarness.FoundItem(Guid.NewGuid()));
        _harness.AddSource("b", "Basement")
            .OnJson("/Sync/Peer/Lookup", ProviderTestHarness.FoundItem(ItemOnB))
            .OnJson("/Items/" + ItemOnB + "/Images", "[{\"ImageType\":\"Primary\"}]");
        var movie = new Movie { Path = LocalFilm, Name = "Film" };
        movie.ProviderIds[PeerMetadataSource.PinnedProviderKey] = "b:" + ItemOnB.ToString("N");

        var images = (await _harness.Source.GetImagesAsync(movie, CancellationToken.None)).ToList();

        Assert.Equal(new[] { "Server Sync (Basement)", "Server Sync (Attic)" }, images.Select(i => i.ProviderName));
    }

    /// <summary>
    /// An item no source has has no images.
    /// True: the image picker shows nothing from Server Sync.
    /// False: a missing item would throw in the image picker.
    /// </summary>
    [Fact]
    public async Task GetImagesAsync_NoSourceHasIt_IsEmpty()
    {
        _harness.AddSource("a").OnJson("/Sync/Peer/Lookup", "{\"Found\":false}");

        var images = await _harness.Source.GetImagesAsync(new Movie { Path = LocalFilm }, CancellationToken.None);

        Assert.Empty(images);
        await Assert.ThrowsAsync<ArgumentNullException>(() => _harness.Source.GetImagesAsync(null!, CancellationToken.None));
    }

    /// <summary>
    /// An image address that is not on a server this one pulls from is refused, and no client is built.
    /// True: the provider only ever fetches from a configured source.
    /// False: a crafted image address could make this server call any host, with a source's API Key.
    /// </summary>
    [Theory]
    [InlineData("https://elsewhere.example/Items/1/Images/Primary/0")]
    [InlineData("https://a.example.evil.test/Items/1/Images/Primary/0")]
    [InlineData("")]
    public async Task GetImageResponseAsync_AddressOffEverySource_IsRefused(string url)
    {
        var server = _harness.AddSource("a");

        await Assert.ThrowsAsync<HttpRequestException>(() => _harness.Source.GetImageResponseAsync(url, CancellationToken.None));

        Assert.Empty(server.Seen);
        Assert.Empty(_harness.Factory.Created);
    }

    /// <summary>
    /// An image address on a source is fetched from that source with its key.
    /// True: Jellyfin can download the artwork it was offered.
    /// False: every offered image would fail to download.
    /// </summary>
    [Fact]
    public async Task GetImageResponseAsync_AddressOnASource_IsFetchedWithItsKey()
    {
        var server = _harness.AddSource("a").OnStatus("/Items/1/Images/Primary/0", HttpStatusCode.OK, "img");
        _harness.AddSource("b");

        using var response = await _harness.Source.GetImageResponseAsync("https://a.example/Items/1/Images/Primary/0", CancellationToken.None);

        Assert.Equal("img", await response.Content.ReadAsStringAsync());
        Assert.Contains("Token=\"k\"", Assert.Single(server.Seen).Authorization, StringComparison.Ordinal);
        Assert.Equal(new[] { "a" }, _harness.Factory.Created);
    }

    private static PeerItemKeys Keys(string? localPath = LocalFilm, Dictionary<string, string>? ids = null)
        => new("Movie", localPath, "Film", ids, null, null, null);
}
