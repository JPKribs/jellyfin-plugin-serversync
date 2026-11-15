using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services.Providers;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Providers;

/// <summary>
/// The series provider must appear in Jellyfin as Server Sync, serve series alone, and fill them through the shared metadata source.
/// </summary>
public sealed class PeerSeriesProviderTests
{
    private static readonly Guid ItemOnSource = Guid.Parse("abababababababababababababababab");

    /// <summary>
    /// The provider carries the shared name and runs first.
    /// True: Series list Server Sync under the one name an operator turns on in the library settings.
    /// False: the provider would show under a name of its own and stay off.
    /// </summary>
    [Fact]
    public void Name_IsTheSharedProviderName()
    {
        var provider = new PeerSeriesProvider(new ProviderTestHarness().Source);

        Assert.Equal(PeerMetadataSource.ProviderName, provider.Name);
        Assert.Equal(0, provider.Order);
    }

    /// <summary>
    /// The provider is the shared base closed over series and their lookup info.
    /// True: Jellyfin offers it for series and for nothing else.
    /// False: it would be offered for another kind of item, or never for series.
    /// </summary>
    [Fact]
    public void Type_ServesSeriesItems()
    {
        Assert.Equal(typeof(PeerMetadataProvider<Series, SeriesInfo>), typeof(PeerSeriesProvider).BaseType);
        Assert.True(typeof(IRemoteMetadataProvider<Series, SeriesInfo>).IsAssignableFrom(typeof(PeerSeriesProvider)));
    }

    /// <summary>
    /// A series a source holds is filled from that source, and the source is asked for that kind of item.
    /// True: a refresh gives the series the source's values.
    /// False: the provider would return nothing, or ask the source for another kind of item.
    /// </summary>
    [Fact]
    public async Task GetMetadata_SourceHoldsTheItem_FillsItThroughTheSharedSource()
    {
        var harness = new ProviderTestHarness();
        var server = harness.AddSourceHolding("a", ItemOnSource, "Series", "From The Source");
        var provider = new PeerSeriesProvider(harness.Source);

        var result = await provider.GetMetadata(new SeriesInfo { Path = ProviderTestHarness.LocalRoot + "/Show" }, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.IsType<Series>(result.Item);
        Assert.Equal("From The Source", result.Item.Name);
        Assert.Contains("\"ItemType\":\"Series\"", Assert.Single(server.To("/Sync/Peer/Lookup")).Body, StringComparison.Ordinal);
    }
}
