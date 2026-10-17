using System;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Queue;

public class LoopBreakerTests
{
    [Fact]
    public void Trips_AfterTheLimit_AndRecoversAfterTheWindow()
    {
        LoopBreaker.Reset();
        var start = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < LoopBreaker.Limit; i++)
        {
            Assert.False(LoopBreaker.Trips(HintKind.Metadata, "loop-key", start.AddSeconds(i)));
        }

        Assert.True(LoopBreaker.Trips(HintKind.Metadata, "loop-key", start.AddSeconds(30)));
        Assert.False(LoopBreaker.Trips(HintKind.Metadata, "other-key", start.AddSeconds(30)));
        Assert.False(LoopBreaker.Trips(HintKind.Metadata, "loop-key", start + LoopBreaker.Window + TimeSpan.FromSeconds(20)));
    }
}
