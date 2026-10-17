using System;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Queue;

public class ApplyGuardRecentTests
{
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

    [Fact]
    public void AppliedRecently_IsFalseForKeysNeverApplied()
    {
        var key = HintProtocol.GuardKey(HintKind.History, Guid.NewGuid().ToString("N"));
        Assert.False(ApplyGuard.AppliedRecently(key, TimeSpan.FromMinutes(10)));
    }
}
