using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Clients;

/// <summary>
/// A pairing must read only its own peer's secret, and must open the wait for a new secret only for as long as it is held.
/// </summary>
public sealed class PeerPairingTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly PeerPairingStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerPairingTests"/> class.
    /// </summary>
    public PeerPairingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-pairing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        _store = new PeerPairingStore(new FixedProvider(_database), new SecretProtector("tests", NullLogger.Instance), NullLogger<PeerPairingStore>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _database.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// The constructor refuses a missing store, peer key, or server id, and keeps this server's id.
    /// True: a pairing always knows whose secret it reads and who it pairs as.
    /// False: a null key would read or open a pairing for no peer at all.
    /// </summary>
    [Fact]
    public void Constructor_Arguments_AreGuardedAndKept()
    {
        var pairing = new PeerPairing(_store, "peer-a", "this-server");

        Assert.Equal("this-server", pairing.ThisServerId);
        Assert.Throws<ArgumentNullException>(() => new PeerPairing(null!, "peer-a", "this-server"));
        Assert.Throws<ArgumentNullException>(() => new PeerPairing(_store, null!, "this-server"));
        Assert.Throws<ArgumentNullException>(() => new PeerPairing(_store, "peer-a", null!));
    }

    /// <summary>
    /// The current secret is the one this peer issued, null before it issued one, and never another peer's or the one this server issued.
    /// True: each peer is sent its own secret.
    /// False: one peer would be sent another's secret, which lets it pose as that peer.
    /// </summary>
    [Fact]
    public void CurrentSecret_OtherSecretsStored_ReturnsOnlyThisPeersOutboundSecret()
    {
        var pairing = new PeerPairing(_store, "peer-a", "this-server");
        var mine = PeerPairingStore.NewSecret();
        _store.SetOutbound("peer-b", PeerPairingStore.NewSecret());
        _store.SetInbound("peer-a", PeerPairingStore.NewSecret());

        var before = pairing.CurrentSecret();
        _store.SetOutbound("peer-a", mine);
        var after = pairing.CurrentSecret();

        Assert.Null(before);
        Assert.Equal(mine, after);
    }

    /// <summary>
    /// Expecting opens the wait for this peer alone, and disposing closes it.
    /// True: the peer may hand over a secret only while this server is asking it to.
    /// False: the wait would stay open for good, or open for every peer at once.
    /// </summary>
    [Fact]
    public void Expect_UntilDisposed_OpensTheWaitForThisPeerOnly()
    {
        var pairing = new PeerPairing(_store, "peer-a", "this-server");

        var open = pairing.Expect();
        var duringA = _store.Expecting("peer-a");
        var duringB = _store.Expecting("peer-b");
        open.Dispose();

        Assert.True(duringA);
        Assert.False(duringB);
        Assert.False(_store.Expecting("peer-a"));
    }
}
