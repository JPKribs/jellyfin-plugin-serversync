using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services.Providers;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Providers;

/// <summary>
/// The image provider must offer a source's images for the kinds of item Server Sync fills, under the shared name, and fetch them only from a source.
/// </summary>
public sealed class PeerImageProviderTests
{
    private static readonly Guid ItemOnSource = Guid.Parse("efefefefefefefefefefefefefefefef");
    private readonly ProviderTestHarness _harness = new();

    /// <summary>
    /// The provider carries the shared name, runs first, and refuses a missing source.
    /// True: images and metadata show as one provider in the library settings.
    /// False: the operator would have to find and enable two differently named providers.
    /// </summary>
    [Fact]
    public void NameAndOrder_AreTheSharedOnes()
    {
        var provider = new PeerImageProvider(_harness.Source);

        Assert.Equal(PeerMetadataSource.ProviderName, provider.Name);
        Assert.Equal(0, provider.Order);
        Assert.Throws<ArgumentNullException>(() => new PeerImageProvider(null!));
    }

    /// <summary>
    /// Each kind of item a metadata provider fills is supported.
    /// True: every item Server Sync can fill can also take its artwork from a source.
    /// False: some kind would get its metadata from a source and its poster from nowhere.
    /// </summary>
    [Theory]
    [InlineData(typeof(Movie))]
    [InlineData(typeof(Series))]
    [InlineData(typeof(Season))]
    [InlineData(typeof(Episode))]
    [InlineData(typeof(MusicAlbum))]
    [InlineData(typeof(MusicArtist))]
    [InlineData(typeof(Audio))]
    [InlineData(typeof(AudioBook))]
    [InlineData(typeof(MusicVideo))]
    [InlineData(typeof(BoxSet))]
    [InlineData(typeof(Person))]
    [InlineData(typeof(Book))]
    [InlineData(typeof(Trailer))]
    [InlineData(typeof(Photo))]
    [InlineData(typeof(PhotoAlbum))]
    [InlineData(typeof(Video))]
    public void Supports_FilledKinds_AreSupported(Type kind)
    {
        var provider = new PeerImageProvider(_harness.Source);

        Assert.True(provider.Supports((BaseItem)Activator.CreateInstance(kind)!));
    }

    /// <summary>
    /// A playlist, a plain folder, and a studio are not supported.
    /// True: Server Sync is not offered as an image source for items it never matches.
    /// False: every folder refresh would ask every source for images it cannot have.
    /// </summary>
    [Theory]
    [InlineData(typeof(Playlist))]
    [InlineData(typeof(Folder))]
    [InlineData(typeof(Studio))]
    [InlineData(typeof(Genre))]
    public void Supports_OtherKinds_AreNot(Type kind)
    {
        var provider = new PeerImageProvider(_harness.Source);

        Assert.False(provider.Supports((BaseItem)Activator.CreateInstance(kind)!));
    }

    /// <summary>
    /// The same eleven image types are offered for every item, and chapter and profile images are not among them.
    /// True: the image picker shows Server Sync under each type a source can hold.
    /// False: a type would be missing from the picker, or chapter images would be requested as artwork.
    /// </summary>
    [Fact]
    public void GetSupportedImages_AnyItem_OffersTheElevenArtworkTypes()
    {
        var provider = new PeerImageProvider(_harness.Source);

        var types = provider.GetSupportedImages(new Movie()).ToList();

        Assert.Equal(
            new[]
            {
                ImageType.Primary, ImageType.Art, ImageType.Backdrop, ImageType.Banner, ImageType.Logo, ImageType.Thumb,
                ImageType.Disc, ImageType.Box, ImageType.BoxRear, ImageType.Menu, ImageType.Screenshot
            },
            types);
        Assert.Equal(types, provider.GetSupportedImages(new Person()));
    }

    /// <summary>
    /// The images of an item a source holds are listed through the shared source, and an item no source has lists none.
    /// True: the picker shows the source's artwork.
    /// False: the provider would offer nothing, or fail for an item no source has.
    /// </summary>
    [Fact]
    public async Task GetImages_SourceHoldsTheItem_ListsItsImages()
    {
        _harness.AddSource("a")
            .OnJson("/Sync/Peer/Lookup", ProviderTestHarness.FoundItem(ItemOnSource))
            .OnJson("/Items/" + ItemOnSource + "/Images", "[{\"ImageType\":\"Logo\",\"ImageIndex\":0}]");
        var provider = new PeerImageProvider(_harness.Source);

        var held = (await provider.GetImages(new Movie { Path = ProviderTestHarness.LocalRoot + "/film.mkv" }, CancellationToken.None)).ToList();
        var missing = await provider.GetImages(new Movie { Path = "/unmapped/film.mkv" }, CancellationToken.None);

        Assert.Equal(ImageType.Logo, Assert.Single(held).Type);
        Assert.Empty(missing);
    }

    /// <summary>
    /// An image is fetched only from a source's own address.
    /// True: a listed image downloads, and an address on another host is refused.
    /// False: the image provider could be made to call any host with a source's API Key.
    /// </summary>
    [Fact]
    public async Task GetImageResponse_OwnAndForeignAddress_FetchesOrRefuses()
    {
        _harness.AddSource("a").OnStatus("/Items/1/Images/Logo/0", HttpStatusCode.OK, "img");
        var provider = new PeerImageProvider(_harness.Source);

        using var response = await provider.GetImageResponse("https://a.example/Items/1/Images/Logo/0", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetImageResponse("https://elsewhere.example/Items/1/Images/Logo/0", CancellationToken.None));
    }
}
