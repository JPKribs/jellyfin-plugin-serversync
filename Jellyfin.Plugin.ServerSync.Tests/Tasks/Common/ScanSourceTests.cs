using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Tasks.Common;

/// <summary>
/// A scan source carries one server entry, its client, and its rank for a run, and releases the client when the run ends.
/// </summary>
public sealed class ScanSourceTests
{
    private static SourceServer Server() => new()
    {
        Key = "server-a",
        Name = "Living Room",
        Url = "http://server-a:8096",
        ApiKey = "k"
    };

    /// <summary>
    /// The source exposes its entry, client, and rank, and takes its key and name from the entry.
    /// True: every row built from the source carries the right server key and logs name the right server.
    /// False: rows would be stamped with another server's key and applied against the wrong source.
    /// </summary>
    [Fact]
    public void Constructor_ValidArguments_ExposesTheEntryAndItsKeyAndName()
    {
        var server = Server();
        using var client = new TaskClientFactory().ClientFor(server);

        using var source = new ScanSource(server, client, 3);

        Assert.Same(server, source.Server);
        Assert.Same(client, source.Client);
        Assert.Equal(3, source.Priority);
        Assert.Equal("server-a", source.Key);
        Assert.Equal("Living Room", source.Name);
    }

    /// <summary>
    /// A missing server entry is refused.
    /// True: a source can never exist without the entry its key comes from.
    /// False: reading the key later would fail deep inside a run.
    /// </summary>
    [Fact]
    public void Constructor_NullServer_Throws()
    {
        var server = Server();
        using var client = new TaskClientFactory().ClientFor(server);

        Assert.Throws<ArgumentNullException>(() => new ScanSource(null!, client, 0));
    }

    /// <summary>
    /// A missing client is refused.
    /// True: a source can never exist without a way to reach its server.
    /// False: the first request of a run would fail with a null reference.
    /// </summary>
    [Fact]
    public void Constructor_NullClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ScanSource(Server(), null!, 0));
    }

    /// <summary>
    /// Disposing the source disposes its client.
    /// True: no client outlives the run that connected it.
    /// False: every run would leak a client for each server.
    /// </summary>
    [Fact]
    public async Task Dispose_DisposesTheClient()
    {
        var server = Server();
        var client = new TaskClientFactory().ClientFor(server);
        var source = new ScanSource(server, client, 0);

        source.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetAllPersonsAsync());
    }
}
