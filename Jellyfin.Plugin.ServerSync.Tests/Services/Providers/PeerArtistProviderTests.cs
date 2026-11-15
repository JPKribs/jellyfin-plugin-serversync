using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services.Providers;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Providers;

/// <summary>
/// The artist provider must appear in Jellyfin as Server Sync, serve artists alone, and fill them through the shared metadata source.
/// </summary>
public sealed class PeerArtistProviderTests
{
    private static readonly Guid ItemOnSource = Guid.Parse("abababababababababababababababab");

    /// <summary>
    /// The provider carries the shared name and runs first.
    /// True: Artists list Server Sync under the one name an operator turns on in the library settings.
    /// False: the provider would show under a name of its own and stay off.
    /// </summary>
    [Fact]
    public void Name_IsTheSharedProviderName()
    {
        var provider = new PeerArtistProvider(new ProviderTestHarness().Source);

        Assert.Equal(PeerMetadataSource.ProviderName, provider.Name);
        Assert.Equal(0, provider.Order);
    }

    /// <summary>
    /// The provider is the shared base closed over artists and their lookup info.
    /// True: Jellyfin offers it for artists and for nothing else.
    /// False: it would be offered for another kind of item, or never for artists.
    /// </summary>
    [Fact]
    public void Type_ServesArtistItems()
    {
        Assert.Equal(typeof(PeerMetadataProvider<MusicArtist, ArtistInfo>), typeof(PeerArtistProvider).BaseType);
        Assert.True(typeof(IRemoteMetadataProvider<MusicArtist, ArtistInfo>).IsAssignableFrom(typeof(PeerArtistProvider)));
    }

    /// <summary>
    /// An artist a source holds is filled from that source, and the source is asked for that kind of item.
    /// True: a refresh gives the artist the source's values.
    /// False: the provider would return nothing, or ask the source for another kind of item.
    /// </summary>
    [Fact]
    public async Task GetMetadata_SourceHoldsTheItem_FillsItThroughTheSharedSource()
    {
        var harness = new ProviderTestHarness();
        var server = harness.AddSourceHolding("a", ItemOnSource, "MusicArtist", "From The Source");
        var provider = new PeerArtistProvider(harness.Source);

        var result = await provider.GetMetadata(new ArtistInfo { Name = "The Band" }, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.IsType<MusicArtist>(result.Item);
        Assert.Equal("From The Source", result.Item.Name);
        Assert.Contains("\"ItemType\":\"MusicArtist\"", Assert.Single(server.To("/Sync/Peer/Lookup")).Body, StringComparison.Ordinal);
    }
}
