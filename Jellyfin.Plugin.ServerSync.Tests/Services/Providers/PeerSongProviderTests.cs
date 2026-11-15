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
/// The song provider must appear in Jellyfin as Server Sync, serve songs alone, and fill them through the shared metadata source.
/// </summary>
public sealed class PeerSongProviderTests
{
    private static readonly Guid ItemOnSource = Guid.Parse("abababababababababababababababab");

    /// <summary>
    /// The provider carries the shared name and runs first.
    /// True: Songs list Server Sync under the one name an operator turns on in the library settings.
    /// False: the provider would show under a name of its own and stay off.
    /// </summary>
    [Fact]
    public void Name_IsTheSharedProviderName()
    {
        var provider = new PeerSongProvider(new ProviderTestHarness().Source);

        Assert.Equal(PeerMetadataSource.ProviderName, provider.Name);
        Assert.Equal(0, provider.Order);
    }

    /// <summary>
    /// The provider is the shared base closed over songs and their lookup info.
    /// True: Jellyfin offers it for songs and for nothing else.
    /// False: it would be offered for another kind of item, or never for songs.
    /// </summary>
    [Fact]
    public void Type_ServesSongItems()
    {
        Assert.Equal(typeof(PeerMetadataProvider<Audio, SongInfo>), typeof(PeerSongProvider).BaseType);
        Assert.True(typeof(IRemoteMetadataProvider<Audio, SongInfo>).IsAssignableFrom(typeof(PeerSongProvider)));
    }

    /// <summary>
    /// A song a source holds is filled from that source, and the source is asked for that kind of item.
    /// True: a refresh gives the song the source's values.
    /// False: the provider would return nothing, or ask the source for another kind of item.
    /// </summary>
    [Fact]
    public async Task GetMetadata_SourceHoldsTheItem_FillsItThroughTheSharedSource()
    {
        var harness = new ProviderTestHarness();
        var server = harness.AddSourceHolding("a", ItemOnSource, "Audio", "From The Source");
        var provider = new PeerSongProvider(harness.Source);

        var result = await provider.GetMetadata(new SongInfo { Path = ProviderTestHarness.LocalRoot + "/Artist/Album/song.flac" }, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.IsType<Audio>(result.Item);
        Assert.Equal("From The Source", result.Item.Name);
        Assert.Contains("\"ItemType\":\"Audio\"", Assert.Single(server.To("/Sync/Peer/Lookup")).Body, StringComparison.Ordinal);
    }
}
