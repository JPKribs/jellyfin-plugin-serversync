using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// A scheduled task's write is guarded while it runs, and afterwards carries the peer's own stamp and never a newer one.
/// </summary>
public sealed class AppliedVersionRecorderTests : IDisposable
{
    private const string PluginVersion = "1.2.3.4";
    private static readonly DateTime PeerEdit = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly QueueTestDatabase _db = new();
    private readonly QueuePeerHandler _peer = new();
    private readonly ApplyGuard _guard = new();
    private readonly VersionStore _versions;
    private readonly AppliedVersionRecorder _recorder;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppliedVersionRecorderTests"/> class.
    /// </summary>
    public AppliedVersionRecorderTests()
    {
        _versions = new VersionStore(_db.Provider, NullLogger<VersionStore>.Instance);
        var resolver = new VersionConflictResolver(_versions, QueueSamples.Host(), new FakeConfigManager { PluginVersion = PluginVersion }, NullLogger<VersionConflictResolver>.Instance);
        _recorder = new AppliedVersionRecorder(_guard, resolver, NullLogger<AppliedVersionRecorder>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose() => _db.Dispose();

    private ScanSource Source() => new(QueueSamples.Server("peer", ServerMode.Pull), _peer.Client(), 0);

    private void PeerKeepsStamps(params ObjectVersion[] held)
    {
        _peer.OnJson("/Sync/Peer/Capabilities", new PeerCapabilities { ServerId = "peer-id", PluginVersion = PluginVersion });
        _peer.OnJson("/Sync/Peer/Versions", new VersionsResponse { Items = new List<ObjectVersion>(held) });
    }

    /// <summary>
    /// A missing collaborator is refused at construction.
    /// True: a wiring mistake fails at startup.
    /// False: it fails inside the first scheduled write.
    /// </summary>
    [Fact]
    public void Constructor_NullCollaborator_Throws()
    {
        var resolver = new VersionConflictResolver(_versions, QueueSamples.Host(), new FakeConfigManager(), NullLogger<VersionConflictResolver>.Instance);

        Assert.Throws<ArgumentNullException>(() => new AppliedVersionRecorder(null!, resolver, NullLogger<AppliedVersionRecorder>.Instance));
        Assert.Throws<ArgumentNullException>(() => new AppliedVersionRecorder(_guard, null!, NullLogger<AppliedVersionRecorder>.Instance));
        Assert.Throws<ArgumentNullException>(() => new AppliedVersionRecorder(_guard, resolver, null!));
    }

    /// <summary>
    /// A write with no key registers nothing.
    /// True: a row that never matched a local object is written without a guard and without a failure.
    /// False: an empty key throws inside the task, or guards every object of the kind.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Enter_NoKey_ReturnsNull(string? localKey)
    {
        var handle = _recorder.Enter(HintKind.Metadata, localKey!);

        Assert.Null(handle);
    }

    /// <summary>
    /// A write is registered with the guard until its handle is disposed.
    /// True: the save event of a scheduled write is known as an echo and raises no hint.
    /// False: every value a scan copies is announced back to the pool.
    /// </summary>
    [Fact]
    public void Enter_WithAKey_GuardsTheObjectUntilDisposed()
    {
        var key = Guid.NewGuid().ToString("N");
        var guardKey = HintProtocol.GuardKey(HintKind.Metadata, key);

        var handle = _recorder.Enter(HintKind.Metadata, key);
        var during = _guard.IsApplying(guardKey);
        handle!.Dispose();

        Assert.True(during);
        Assert.False(_guard.IsApplying(guardKey));
    }

    /// <summary>
    /// A write registered for a whole kind covers every object of that kind and no other kind.
    /// True: a write with side effects on other items of the same type stays quiet about all of them.
    /// False: the side effects are announced, or a different sync type is silenced too.
    /// </summary>
    [Fact]
    public void EnterAll_ForAKind_GuardsEveryKeyOfThatKindOnly()
    {
        using var handle = _recorder.EnterAll(HintKind.People);

        Assert.True(_guard.IsApplying(HintProtocol.GuardKey(HintKind.People, "ANYONE")));
        Assert.False(_guard.IsApplying(HintProtocol.GuardKey(HintKind.Metadata, "ANYONE")));
    }

    /// <summary>
    /// With no local key or no source nothing is recorded and the peer is not asked.
    /// True: a row that matched nothing here leaves no stamp.
    /// False: a stamp is stored under an empty key, or the task fails on a missing source.
    /// </summary>
    [Theory]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("local", false)]
    public async Task RecordAsync_NoKeyOrNoSource_RecordsNothing(string? localKey, bool withSource)
    {
        PeerKeepsStamps(new ObjectVersion { Kind = HintKind.Users, Key = "peer-key", ServerId = "peer-id", Timestamp = PeerEdit });
        using var source = Source();

        await _recorder.RecordAsync(HintKind.Users, localKey!, "peer-key", withSource ? source : null, CancellationToken.None);

        Assert.Empty(_peer.Requests);
        Assert.Empty(_versions.GetKeys(HintKind.Users));
    }

    /// <summary>
    /// The stamp the scan read with the value is recorded, and the peer is not asked again.
    /// True: the stamp stored belongs to the value that was written.
    /// False: a stale value is stored under whatever newer stamp the peer holds by then.
    /// </summary>
    [Fact]
    public async Task RecordAsync_StampPendingFromTheScan_PromotesItWithoutAskingThePeer()
    {
        _versions.SetPending(new ObjectVersion { Kind = HintKind.Metadata, Key = "local", ServerId = "peer-id", Timestamp = PeerEdit });
        using var source = Source();

        await _recorder.RecordAsync(HintKind.Metadata, "local", "peer-key", source, CancellationToken.None);

        var stored = _versions.Get(HintKind.Metadata, "local");
        Assert.Equal("peer-id", stored?.ServerId);
        Assert.Equal(PeerEdit, stored?.Timestamp);
        Assert.Empty(_peer.Requests);
    }

    /// <summary>
    /// A value of any kind but Users with no pending stamp records nothing and asks nothing.
    /// True: a value the source never stamped leaves none here, so any edit beats it.
    /// False: a copied value is stamped as an edit and wins conflicts it should lose.
    /// </summary>
    [Fact]
    public async Task RecordAsync_NotUsersAndNothingPending_RecordsNothing()
    {
        PeerKeepsStamps(new ObjectVersion { Kind = HintKind.Metadata, Key = "peer-key", ServerId = "peer-id", Timestamp = PeerEdit });
        using var source = Source();

        await _recorder.RecordAsync(HintKind.Metadata, "local", "peer-key", source, CancellationToken.None);

        Assert.Null(_versions.Get(HintKind.Metadata, "local"));
        Assert.Empty(_peer.Requests);
    }

    /// <summary>
    /// For user settings the peer's stamp is read and stored under this server's key.
    /// True: copied user settings carry the edit they came from, and no newer one.
    /// False: a copy looks like a fresh edit here and is pushed back over the original.
    /// </summary>
    [Fact]
    public async Task RecordAsync_UsersWithAPeerStamp_RecordsItUnderTheLocalKey()
    {
        PeerKeepsStamps(new ObjectVersion { Kind = HintKind.Users, Key = "peer-key", ServerId = "peer-id", Timestamp = PeerEdit });
        using var source = Source();

        await _recorder.RecordAsync(HintKind.Users, "local", "peer-key", source, CancellationToken.None);

        var stored = _versions.Get(HintKind.Users, "local");
        Assert.NotNull(stored);
        Assert.Equal("peer-id", stored!.ServerId);
        Assert.Equal(PeerEdit, stored.Timestamp);
        Assert.Null(_versions.Get(HintKind.Users, "peer-key"));
    }

    /// <summary>
    /// User settings the peer never stamped leave no stamp here.
    /// True: an unstamped value can be beaten by any edit.
    /// False: a made up stamp protects a value nobody edited.
    /// </summary>
    [Fact]
    public async Task RecordAsync_UsersWithNoPeerStamp_RecordsNothing()
    {
        PeerKeepsStamps();
        using var source = Source();

        await _recorder.RecordAsync(HintKind.Users, "local", "peer-key", source, CancellationToken.None);

        Assert.Null(_versions.Get(HintKind.Users, "local"));
        Assert.Equal(1, _peer.CountOf("/Sync/Peer/Versions"));
    }

    /// <summary>
    /// A peer without Server Sync is not asked for stamps.
    /// True: a plain Jellyfin source costs one capability check and nothing more.
    /// False: every copied row sends a request the source can only refuse.
    /// </summary>
    [Fact]
    public async Task RecordAsync_UsersFromAPeerWithoutServerSync_DoesNotAskForStamps()
    {
        using var source = Source();

        await _recorder.RecordAsync(HintKind.Users, "local", "peer-key", source, CancellationToken.None);

        Assert.Null(_versions.Get(HintKind.Users, "local"));
        Assert.Equal(0, _peer.CountOf("/Sync/Peer/Versions"));
    }

    /// <summary>
    /// A peer that fails the stamp read does not fail the row.
    /// True: the value is already copied, so the row still ends synced.
    /// False: a failing stamp read marks a finished copy as errored and it is applied again.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecordAsync_PeerFailsTheStampRead_DoesNotThrow(bool answersWithAnError)
    {
        _peer.OnJson("/Sync/Peer/Capabilities", new PeerCapabilities { ServerId = "peer-id", PluginVersion = PluginVersion });
        if (answersWithAnError)
        {
            _peer.OnStatus("/Sync/Peer/Versions", HttpStatusCode.InternalServerError);
        }
        else
        {
            _peer.OnUnreachable("/Sync/Peer/Versions");
        }

        using var source = Source();

        await _recorder.RecordAsync(HintKind.Users, "local", "peer-key", source, CancellationToken.None);

        Assert.Null(_versions.Get(HintKind.Users, "local"));
    }

    /// <summary>
    /// A cancelled record stops and says so.
    /// True: a task that is being stopped ends at once.
    /// False: the cancellation is swallowed and the task runs on after it was told to stop.
    /// </summary>
    [Fact]
    public async Task RecordAsync_CancelledToken_Throws()
    {
        PeerKeepsStamps();
        using var source = Source();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _recorder.RecordAsync(HintKind.Users, "local", "peer-key", source, cancelled.Token));
    }
}
