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
/// The photo album provider must appear in Jellyfin as Server Sync, serve photo albums alone, and fill them through the shared metadata source.
/// </summary>
public sealed class PeerPhotoAlbumProviderTests
{
    private static readonly Guid ItemOnSource = Guid.Parse("abababababababababababababababab");

    /// <summary>
    /// The provider carries the shared name and runs first.
    /// True: Photo albums list Server Sync under the one name an operator turns on in the library settings.
    /// False: the provider would show under a name of its own and stay off.
    /// </summary>
    [Fact]
    public void Name_IsTheSharedProviderName()
    {
        var provider = new PeerPhotoAlbumProvider(new ProviderTestHarness().Source);

        Assert.Equal(PeerMetadataSource.ProviderName, provider.Name);
        Assert.Equal(0, provider.Order);
    }

    /// <summary>
    /// The provider is the shared base closed over photo albums and their lookup info.
    /// True: Jellyfin offers it for photo albums and for nothing else.
    /// False: it would be offered for another kind of item, or never for photo albums.
    /// </summary>
    [Fact]
    public void Type_ServesPhotoAlbumItems()
    {
        Assert.Equal(typeof(PeerMetadataProvider<PhotoAlbum, ItemLookupInfo>), typeof(PeerPhotoAlbumProvider).BaseType);
        Assert.True(typeof(IRemoteMetadataProvider<PhotoAlbum, ItemLookupInfo>).IsAssignableFrom(typeof(PeerPhotoAlbumProvider)));
    }

    /// <summary>
    /// A photo album a source holds is filled from that source, and the source is asked for that kind of item.
    /// True: a refresh gives the photo album the source's values.
    /// False: the provider would return nothing, or ask the source for another kind of item.
    /// </summary>
    [Fact]
    public async Task GetMetadata_SourceHoldsTheItem_FillsItThroughTheSharedSource()
    {
        var harness = new ProviderTestHarness();
        var server = harness.AddSourceHolding("a", ItemOnSource, "PhotoAlbum", "From The Source");
        var provider = new PeerPhotoAlbumProvider(harness.Source);

        var result = await provider.GetMetadata(new ItemLookupInfo { Path = ProviderTestHarness.LocalRoot + "/Holiday" }, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.IsType<PhotoAlbum>(result.Item);
        Assert.Equal("From The Source", result.Item.Name);
        Assert.Contains("\"ItemType\":\"PhotoAlbum\"", Assert.Single(server.To("/Sync/Peer/Lookup")).Body, StringComparison.Ordinal);
    }
}
