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
/// The season provider must appear in Jellyfin as Server Sync, serve seasons alone, and fill them through the shared metadata source.
/// </summary>
public sealed class PeerSeasonProviderTests
{
    private static readonly Guid ItemOnSource = Guid.Parse("abababababababababababababababab");

    /// <summary>
    /// The provider carries the shared name and runs first.
    /// True: Seasons list Server Sync under the one name an operator turns on in the library settings.
    /// False: the provider would show under a name of its own and stay off.
    /// </summary>
    [Fact]
    public void Name_IsTheSharedProviderName()
    {
        var provider = new PeerSeasonProvider(new ProviderTestHarness().Source);

        Assert.Equal(PeerMetadataSource.ProviderName, provider.Name);
        Assert.Equal(0, provider.Order);
    }

    /// <summary>
    /// The provider is the shared base closed over seasons and their lookup info.
    /// True: Jellyfin offers it for seasons and for nothing else.
    /// False: it would be offered for another kind of item, or never for seasons.
    /// </summary>
    [Fact]
    public void Type_ServesSeasonItems()
    {
        Assert.Equal(typeof(PeerMetadataProvider<Season, SeasonInfo>), typeof(PeerSeasonProvider).BaseType);
        Assert.True(typeof(IRemoteMetadataProvider<Season, SeasonInfo>).IsAssignableFrom(typeof(PeerSeasonProvider)));
    }

    /// <summary>
    /// A season a source holds is filled from that source, and the source is asked for that kind of item.
    /// True: a refresh gives the season the source's values.
    /// False: the provider would return nothing, or ask the source for another kind of item.
    /// </summary>
    [Fact]
    public async Task GetMetadata_SourceHoldsTheItem_FillsItThroughTheSharedSource()
    {
        var harness = new ProviderTestHarness();
        var server = harness.AddSourceHolding("a", ItemOnSource, "Season", "From The Source");
        var provider = new PeerSeasonProvider(harness.Source);

        var result = await provider.GetMetadata(new SeasonInfo { Path = ProviderTestHarness.LocalRoot + "/Show/Season 1" }, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.IsType<Season>(result.Item);
        Assert.Equal("From The Source", result.Item.Name);
        Assert.Contains("\"ItemType\":\"Season\"", Assert.Single(server.To("/Sync/Peer/Lookup")).Body, StringComparison.Ordinal);
    }
}
