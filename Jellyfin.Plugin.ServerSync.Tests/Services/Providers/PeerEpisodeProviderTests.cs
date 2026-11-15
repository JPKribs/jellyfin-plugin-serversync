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
/// The episode provider must appear in Jellyfin as Server Sync, serve episodes alone, and fill them through the shared metadata source.
/// </summary>
public sealed class PeerEpisodeProviderTests
{
    private static readonly Guid ItemOnSource = Guid.Parse("abababababababababababababababab");

    /// <summary>
    /// The provider carries the shared name and runs first.
    /// True: Episodes list Server Sync under the one name an operator turns on in the library settings.
    /// False: the provider would show under a name of its own and stay off.
    /// </summary>
    [Fact]
    public void Name_IsTheSharedProviderName()
    {
        var provider = new PeerEpisodeProvider(new ProviderTestHarness().Source);

        Assert.Equal(PeerMetadataSource.ProviderName, provider.Name);
        Assert.Equal(0, provider.Order);
    }

    /// <summary>
    /// The provider is the shared base closed over episodes and their lookup info.
    /// True: Jellyfin offers it for episodes and for nothing else.
    /// False: it would be offered for another kind of item, or never for episodes.
    /// </summary>
    [Fact]
    public void Type_ServesEpisodeItems()
    {
        Assert.Equal(typeof(PeerMetadataProvider<Episode, EpisodeInfo>), typeof(PeerEpisodeProvider).BaseType);
        Assert.True(typeof(IRemoteMetadataProvider<Episode, EpisodeInfo>).IsAssignableFrom(typeof(PeerEpisodeProvider)));
    }

    /// <summary>
    /// An episode a source holds is filled from that source, and the source is asked for that kind of item.
    /// True: a refresh gives the episode the source's values.
    /// False: the provider would return nothing, or ask the source for another kind of item.
    /// </summary>
    [Fact]
    public async Task GetMetadata_SourceHoldsTheItem_FillsItThroughTheSharedSource()
    {
        var harness = new ProviderTestHarness();
        var server = harness.AddSourceHolding("a", ItemOnSource, "Episode", "From The Source");
        var provider = new PeerEpisodeProvider(harness.Source);

        var result = await provider.GetMetadata(new EpisodeInfo { Path = ProviderTestHarness.LocalRoot + "/Show/S01E01.mkv" }, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.IsType<Episode>(result.Item);
        Assert.Equal("From The Source", result.Item.Name);
        Assert.Contains("\"ItemType\":\"Episode\"", Assert.Single(server.To("/Sync/Peer/Lookup")).Body, StringComparison.Ordinal);
    }
}
