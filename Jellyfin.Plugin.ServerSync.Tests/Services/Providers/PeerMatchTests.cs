using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Providers;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Providers;

/// <summary>
/// A match must name where an item was found, and must release the client that found it when disposed.
/// </summary>
public sealed class PeerMatchTests
{
    private static readonly Guid ItemId = Guid.Parse("12121212121212121212121212121212");

    /// <summary>
    /// A match keeps the source, the client, and the item's id it was built with.
    /// True: the caller reads the item from the server that has it, with the client that reached it.
    /// False: the item would be read from another server, where the id means something else.
    /// </summary>
    [Fact]
    public void Constructor_Parts_AreKept()
    {
        var parent = new SourceServer { Key = "a" };
        using var client = new ClientStubServer().Client();

        using var match = new PeerMatch(parent, client, ItemId);

        Assert.Same(parent, match.Parent);
        Assert.Same(client, match.Client);
        Assert.Equal(ItemId, match.ItemId);
    }

    /// <summary>
    /// Disposing a match disposes its client.
    /// True: every lookup releases the client it opened.
    /// False: each refreshed item would leak a client.
    /// </summary>
    [Fact]
    public async Task Dispose_Match_DisposesItsClient()
    {
        var server = new ClientStubServer().OnJson("/Items", ClientJson.Page(1, ClientJson.Item(ItemId)));
        var client = server.Client();
        var match = new PeerMatch(new SourceServer { Key = "a" }, client, ItemId);
        var before = await client.FindItemDetailsAsync(ItemId);

        match.Dispose();

        Assert.NotNull(before);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.FindItemDetailsAsync(ItemId));
    }
}
