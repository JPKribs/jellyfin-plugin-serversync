using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Peer;

public sealed class PeerPairingStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly PeerPairingStore _store;

    public PeerPairingStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        var secrets = new SecretProtector("Jellyfin.Plugin.ServerSync.Tests", NullLogger.Instance);
        _store = new PeerPairingStore(new FixedProvider(_database), secrets, NullLogger<PeerPairingStore>.Instance);
    }

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
    /// A secret is long, URL safe, and never the same twice.
    /// True: a header carries it and a guess never lands.
    /// False: pairing proves nothing.
    /// </summary>
    [Fact]
    public void NewSecret_IsLongUrlSafeAndUnique()
    {
        var a = PeerPairingStore.NewSecret();
        var b = PeerPairingStore.NewSecret();
        Assert.True(a.Length >= 40);
        Assert.NotEqual(a, b);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain('=', a);
    }

    /// <summary>
    /// Each direction round trips on its own, a missing side reads as null, and forgetting clears both.
    /// True: the receiver checks what it issued and the sender presents what it was given.
    /// False: one direction overwrites the other and every request after the second pairing fails.
    /// </summary>
    [Fact]
    public void Secrets_RoundTripPerDirection()
    {
        Assert.Null(_store.GetInbound("peer"));
        Assert.Null(_store.GetOutbound("peer"));

        _store.SetInbound("peer", "in-1");
        _store.SetOutbound("peer", "out-1");
        Assert.Equal("in-1", _store.GetInbound("peer"));
        Assert.Equal("out-1", _store.GetOutbound("peer"));

        _store.SetInbound("peer", "in-2");
        Assert.Equal("in-2", _store.GetInbound("peer"));
        Assert.Equal("out-1", _store.GetOutbound("peer"));

        _store.Remove("peer");
        Assert.Null(_store.GetInbound("peer"));
        Assert.Null(_store.GetOutbound("peer"));
    }

    /// <summary>
    /// A refused pairing is remembered until a secret is issued.
    /// True: a peer this server holds a standard user's key for is taken on its word, and a later
    /// administrator's key makes it strict again.
    /// False: such a peer is refused forever, or a refusal lingers after pairing succeeds.
    /// </summary>
    [Fact]
    public void Refusal_IsRememberedUntilPaired()
    {
        Assert.Null(_store.InboundRefusedAt("peer"));
        Assert.Null(_store.InboundRefusedAt("peer"));
        _store.MarkInboundRefused("peer");
        Assert.NotNull(_store.InboundRefusedAt("peer"));
        Assert.True(_store.InboundRefusedAt("peer") > DateTime.UtcNow.AddMinutes(-1));
        Assert.Null(_store.GetInbound("peer"));

        _store.SetInbound("peer", "in-1");
        Assert.Null(_store.InboundRefusedAt("peer"));
        Assert.Equal("in-1", _store.GetInbound("peer"));
    }

    /// <summary>
    /// Matching is exact and never true for a missing side.
    /// True: a request without the header, or with someone else's secret, is refused.
    /// False: an empty header matches an empty store and anyone is "paired".
    /// </summary>
    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData("abc", "abcd", false)]
    [InlineData(null, "abc", false)]
    [InlineData("abc", null, false)]
    [InlineData("", "", false)]
    [InlineData(null, null, false)]
    public void Matches_IsExactAndNeverEmpty(string? presented, string? expected, bool matches)
    {
        Assert.Equal(matches, PeerPairingStore.Matches(presented, expected));
    }

    private sealed class FixedProvider : ISyncDatabaseProvider
    {
        public FixedProvider(SyncDatabase db) => Database = db;

        public SyncDatabase Database { get; }
    }
}
