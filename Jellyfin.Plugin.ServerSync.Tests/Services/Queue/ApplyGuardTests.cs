using System;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// The apply guard marks the keys this server is writing so the observer does not hint its own applies back.
/// </summary>
public class ApplyGuardTests
{
    /// <summary>
    /// A key is applying only between enter and dispose, and nested enters count.
    /// True: the observer stays quiet exactly for the duration of an apply.
    /// False: an apply's echo would be hinted back, or a real edit after it would be swallowed.
    /// </summary>
    [Fact]
    public void Enter_TracksNestedScopes()
    {
        var guard = new ApplyGuard();
        Assert.False(guard.IsApplying("k"));

        using var outer = guard.Enter("k");
        using var inner = guard.Enter("k");
        Assert.True(guard.IsApplying("k"));
        inner.Dispose();
        Assert.True(guard.IsApplying("k"));
        outer.Dispose();
        Assert.False(guard.IsApplying("k"));

        outer.Dispose();
        Assert.False(guard.IsApplying("k"));
    }

    /// <summary>
    /// A registration for a whole kind covers every key of that kind and no other kind.
    /// True: writing an item's cast holds back the people hints its side effects would raise.
    /// False: every metadata apply would hint every cast member to every peer.
    /// </summary>
    [Fact]
    public void GuardAll_CoversTheKind()
    {
        var guard = new ApplyGuard();
        using (guard.Enter(HintProtocol.GuardAllKey(HintKind.People)))
        {
            Assert.True(guard.IsApplying(HintProtocol.GuardKey(HintKind.People, "ALICE")));
            Assert.False(guard.IsApplying(HintProtocol.GuardKey(HintKind.Metadata, "ALICE")));
        }

        Assert.False(guard.IsApplying(HintProtocol.GuardKey(HintKind.People, "ALICE")));
    }

    /// <summary>
    /// A key-wide apply leaves every key of that kind marked as applied for a while after it ends.
    /// True: a playlist written by a kind-wide apply is not hinted back as a local edit.
    /// False: the echo of every kind-wide apply is hinted to the peers again.
    /// </summary>
    [Fact]
    public void AppliedRecently_CoversKeysOfAKindWideApply()
    {
        var guard = new ApplyGuard();
        var key = HintProtocol.GuardKey(HintKind.Playlists, Guid.NewGuid().ToString("N"));
        using (guard.Enter(HintProtocol.GuardAllKey(HintKind.Playlists)))
        {
        }

        Assert.True(ApplyGuard.AppliedRecently(key, TimeSpan.FromMinutes(10)));
    }

    /// <summary>
    /// A key that was never applied is not recently applied.
    /// True: a real local edit to an untouched key is hinted.
    /// False: every edit is swallowed as an apply echo.
    /// </summary>
    [Fact]
    public void AppliedRecently_IsFalseForKeysNeverApplied()
    {
        var key = HintProtocol.GuardKey(HintKind.History, Guid.NewGuid().ToString("N"));

        Assert.False(ApplyGuard.AppliedRecently(key, TimeSpan.FromMinutes(10)));
    }
}
