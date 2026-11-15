using System;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// The loop breaker stops a hint that keeps coming back for the same object.
/// </summary>
[Collection("LoopBreaker")]
public class LoopBreakerTests
{
    /// <summary>
    /// A key trips once it is hinted more than the limit inside the window, and recovers when the window passes.
    /// True: an object bouncing between servers is cut off, and other keys and later windows are untouched.
    /// False: a hint loop runs forever, or one noisy key silences every other key.
    /// </summary>
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
