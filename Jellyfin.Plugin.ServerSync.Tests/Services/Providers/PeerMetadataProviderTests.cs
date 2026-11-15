using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services.Providers;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Providers;

/// <summary>
/// The shared provider base must pass every question to the metadata source as its own kind of item, and report "no metadata" when no source has it.
/// </summary>
public sealed class PeerMetadataProviderTests
{
    private static readonly Guid ItemOnSource = Guid.Parse("cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd");
    private readonly ProviderTestHarness _harness = new();

    /// <summary>
    /// A provider refuses a missing metadata source.
    /// True: a broken registration fails when Jellyfin builds its providers.
    /// False: the first refresh of any item would fail with a null reference.
    /// </summary>
    [Fact]
    public void Constructor_MissingSource_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PeerSeriesProvider(null!));
    }

    /// <summary>
    /// With no source that has the item, the answer is "no metadata" under the provider's name.
    /// True: Jellyfin moves on to its other providers.
    /// False: an item no source has would fail its refresh.
    /// </summary>
    [Fact]
    public async Task GetMetadata_NoSourceHasTheItem_HasNoMetadata()
    {
        var provider = new PeerSeriesProvider(_harness.Source);

        var result = await provider.GetMetadata(new SeriesInfo { Path = ProviderTestHarness.LocalRoot + "/Show" }, CancellationToken.None);

        Assert.False(result.HasMetadata);
        Assert.Null(result.Item);
        Assert.Equal(PeerMetadataSource.ProviderName, result.Provider);
    }

    /// <summary>
    /// A search asks each source for the provider's own kind of item and returns one result per source that has it.
    /// True: Identify on a series lists series, one per server.
    /// False: a series search would ask for movies and match the wrong items.
    /// </summary>
    [Fact]
    public async Task GetSearchResults_SourceHoldsTheItem_AsksForItsOwnKind()
    {
        var server = _harness.AddSourceHolding("a", ItemOnSource, "Series", "The Show");
        var provider = new PeerSeriesProvider(_harness.Source);

        var results = (await provider.GetSearchResults(new SeriesInfo { Path = ProviderTestHarness.LocalRoot + "/Show" }, CancellationToken.None)).ToList();

        Assert.Equal("The Show on Server a", Assert.Single(results).Name);
        Assert.Contains("\"ItemType\":\"Series\"", Assert.Single(server.To("/Sync/Peer/Lookup")).Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// An image is fetched only from a source's own address.
    /// True: a search result's image loads, and an address on another host is refused.
    /// False: a provider could be made to call any host with a source's API Key.
    /// </summary>
    [Fact]
    public async Task GetImageResponse_OwnAndForeignAddress_FetchesOrRefuses()
    {
        _harness.AddSource("a").OnStatus("/Items/1/Images/Primary", HttpStatusCode.OK, "img");
        var provider = new PeerSeriesProvider(_harness.Source);

        using var response = await provider.GetImageResponse("https://a.example/Items/1/Images/Primary", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetImageResponse("https://elsewhere.example/Items/1/Images/Primary", CancellationToken.None));
    }
}
