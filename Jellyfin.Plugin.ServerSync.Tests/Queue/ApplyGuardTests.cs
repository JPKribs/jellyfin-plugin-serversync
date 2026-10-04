using Jellyfin.Plugin.ServerSync.Services.Queue;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Queue;

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

        var outer = guard.Enter("k");
        var inner = guard.Enter("k");
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
        using (guard.Enter(HintProtocol.GuardAllKey(Jellyfin.Plugin.ServerSync.Models.Queue.HintKind.People)))
        {
            Assert.True(guard.IsApplying(HintProtocol.GuardKey(Jellyfin.Plugin.ServerSync.Models.Queue.HintKind.People, "ALICE")));
            Assert.False(guard.IsApplying(HintProtocol.GuardKey(Jellyfin.Plugin.ServerSync.Models.Queue.HintKind.Metadata, "ALICE")));
        }

        Assert.False(guard.IsApplying(HintProtocol.GuardKey(Jellyfin.Plugin.ServerSync.Models.Queue.HintKind.People, "ALICE")));
    }
}
