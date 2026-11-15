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
/// The person provider must appear in Jellyfin as Server Sync, serve people alone, and fill them through the shared metadata source.
/// </summary>
public sealed class PeerPersonProviderTests
{
    private static readonly Guid ItemOnSource = Guid.Parse("abababababababababababababababab");

    /// <summary>
    /// The provider carries the shared name and runs first.
    /// True: People list Server Sync under the one name an operator turns on in the library settings.
    /// False: the provider would show under a name of its own and stay off.
    /// </summary>
    [Fact]
    public void Name_IsTheSharedProviderName()
    {
        var provider = new PeerPersonProvider(new ProviderTestHarness().Source);

        Assert.Equal(PeerMetadataSource.ProviderName, provider.Name);
        Assert.Equal(0, provider.Order);
    }

    /// <summary>
    /// The provider is the shared base closed over people and their lookup info.
    /// True: Jellyfin offers it for people and for nothing else.
    /// False: it would be offered for another kind of item, or never for people.
    /// </summary>
    [Fact]
    public void Type_ServesPersonItems()
    {
        Assert.Equal(typeof(PeerMetadataProvider<Person, PersonLookupInfo>), typeof(PeerPersonProvider).BaseType);
        Assert.True(typeof(IRemoteMetadataProvider<Person, PersonLookupInfo>).IsAssignableFrom(typeof(PeerPersonProvider)));
    }

    /// <summary>
    /// A person a source holds is filled from that source, and the source is asked for that kind of item.
    /// True: a refresh gives the person the source's values.
    /// False: the provider would return nothing, or ask the source for another kind of item.
    /// </summary>
    [Fact]
    public async Task GetMetadata_SourceHoldsTheItem_FillsItThroughTheSharedSource()
    {
        var harness = new ProviderTestHarness();
        var server = harness.AddSourceHolding("a", ItemOnSource, "Person", "From The Source");
        var provider = new PeerPersonProvider(harness.Source);

        var result = await provider.GetMetadata(new PersonLookupInfo { Name = "Ada Lovelace" }, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.IsType<Person>(result.Item);
        Assert.Equal("From The Source", result.Item.Name);
        Assert.Contains("\"ItemType\":\"Person\"", Assert.Single(server.To("/Sync/Peer/Lookup")).Body, StringComparison.Ordinal);
    }
}
