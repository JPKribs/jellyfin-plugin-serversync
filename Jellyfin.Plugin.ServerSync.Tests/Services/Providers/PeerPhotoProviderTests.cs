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
/// The photo provider must appear in Jellyfin as Server Sync, serve photos alone, and fill them through the shared metadata source.
/// </summary>
public sealed class PeerPhotoProviderTests
{
    private static readonly Guid ItemOnSource = Guid.Parse("abababababababababababababababab");

    /// <summary>
    /// The provider carries the shared name and runs first.
    /// True: Photos list Server Sync under the one name an operator turns on in the library settings.
    /// False: the provider would show under a name of its own and stay off.
    /// </summary>
    [Fact]
    public void Name_IsTheSharedProviderName()
    {
        var provider = new PeerPhotoProvider(new ProviderTestHarness().Source);

        Assert.Equal(PeerMetadataSource.ProviderName, provider.Name);
        Assert.Equal(0, provider.Order);
    }

    /// <summary>
    /// The provider is the shared base closed over photos and their lookup info.
    /// True: Jellyfin offers it for photos and for nothing else.
    /// False: it would be offered for another kind of item, or never for photos.
    /// </summary>
    [Fact]
    public void Type_ServesPhotoItems()
    {
        Assert.Equal(typeof(PeerMetadataProvider<Photo, ItemLookupInfo>), typeof(PeerPhotoProvider).BaseType);
        Assert.True(typeof(IRemoteMetadataProvider<Photo, ItemLookupInfo>).IsAssignableFrom(typeof(PeerPhotoProvider)));
    }

    /// <summary>
    /// A photo a source holds is filled from that source, and the source is asked for that kind of item.
    /// True: a refresh gives the photo the source's values.
    /// False: the provider would return nothing, or ask the source for another kind of item.
    /// </summary>
    [Fact]
    public async Task GetMetadata_SourceHoldsTheItem_FillsItThroughTheSharedSource()
    {
        var harness = new ProviderTestHarness();
        var server = harness.AddSourceHolding("a", ItemOnSource, "Photo", "From The Source");
        var provider = new PeerPhotoProvider(harness.Source);

        var result = await provider.GetMetadata(new ItemLookupInfo { Path = ProviderTestHarness.LocalRoot + "/photo.jpg" }, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.IsType<Photo>(result.Item);
        Assert.Equal("From The Source", result.Item.Name);
        Assert.Contains("\"ItemType\":\"Photo\"", Assert.Single(server.To("/Sync/Peer/Lookup")).Body, StringComparison.Ordinal);
    }
}
