using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services.Providers;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Providers;

/// <summary>
/// The video provider must appear in Jellyfin as Server Sync, serve videos alone, and fill them through the shared metadata source.
/// </summary>
public sealed class PeerVideoProviderTests
{
    private static readonly Guid ItemOnSource = Guid.Parse("abababababababababababababababab");

    /// <summary>
    /// The provider carries the shared name and runs first.
    /// True: Videos list Server Sync under the one name an operator turns on in the library settings.
    /// False: the provider would show under a name of its own and stay off.
    /// </summary>
    [Fact]
    public void Name_IsTheSharedProviderName()
    {
        var provider = new PeerVideoProvider(new ProviderTestHarness().Source);

        Assert.Equal(PeerMetadataSource.ProviderName, provider.Name);
        Assert.Equal(0, provider.Order);
    }

    /// <summary>
    /// The provider is the shared base closed over videos and their lookup info.
    /// True: Jellyfin offers it for videos and for nothing else.
    /// False: it would be offered for another kind of item, or never for videos.
    /// </summary>
    [Fact]
    public void Type_ServesVideoItems()
    {
        Assert.Equal(typeof(PeerMetadataProvider<Video, ItemLookupInfo>), typeof(PeerVideoProvider).BaseType);
        Assert.True(typeof(IRemoteMetadataProvider<Video, ItemLookupInfo>).IsAssignableFrom(typeof(PeerVideoProvider)));
    }

    /// <summary>
    /// A video a source holds is filled from that source, and the source is asked for that kind of item.
    /// True: a refresh gives the video the source's values.
    /// False: the provider would return nothing, or ask the source for another kind of item.
    /// </summary>
    [Fact]
    public async Task GetMetadata_SourceHoldsTheItem_FillsItThroughTheSharedSource()
    {
        var harness = new ProviderTestHarness();
        var server = harness.AddSourceHolding("a", ItemOnSource, "Video", "From The Source");
        var provider = new PeerVideoProvider(harness.Source);

        var result = await provider.GetMetadata(new ItemLookupInfo { Path = ProviderTestHarness.LocalRoot + "/clip.mp4" }, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.IsType<Video>(result.Item);
        Assert.Equal("From The Source", result.Item.Name);
        Assert.Contains("\"ItemType\":\"Video\"", Assert.Single(server.To("/Sync/Peer/Lookup")).Body, StringComparison.Ordinal);
    }
}
