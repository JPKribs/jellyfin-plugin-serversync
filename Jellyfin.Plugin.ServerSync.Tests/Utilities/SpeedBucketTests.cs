using System;
using Jellyfin.Plugin.ServerSync.Utilities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

/// <summary>
/// The speed bucket lets a stream burst for a few seconds, then holds it to its rate, and it reads its time from a clock the test controls.
/// </summary>
public class SpeedBucketTests
{
    private sealed class Clock
    {
        public TimeSpan Now { get; set; }
    }

    /// <summary>
    /// A new bucket holds three seconds of burst, so a read inside it waits for nothing.
    /// True: the first three seconds of a stream at 100 bytes a second go through unthrottled.
    /// False: every stream would crawl from its first byte.
    /// </summary>
    [Fact]
    public void Take_WithinTheBurst_DoesNotWait()
    {
        var clock = new Clock();
        var bucket = new SpeedBucket(100, () => clock.Now);

        var wait = bucket.Take(300);

        Assert.Equal(TimeSpan.Zero, wait);
    }

    /// <summary>
    /// A read past the burst waits for the time the missing bytes take at the rate.
    /// True: 100 bytes over the burst at 100 bytes a second waits one second.
    /// False: a stream would run faster than its limit.
    /// </summary>
    [Fact]
    public void Take_PastTheBurst_WaitsForTheDebt()
    {
        var clock = new Clock();
        var bucket = new SpeedBucket(100, () => clock.Now);
        bucket.Take(300);

        var wait = bucket.Take(100);

        Assert.Equal(TimeSpan.FromSeconds(1), wait);
    }

    /// <summary>
    /// Time passing pays the debt back at the rate.
    /// True: after two seconds a 100 byte debt is cleared and 100 bytes are free again.
    /// False: a stream that slowed down would never speed up.
    /// </summary>
    [Fact]
    public void Take_AfterTimePasses_RepaysTheDebt()
    {
        var clock = new Clock();
        var bucket = new SpeedBucket(100, () => clock.Now);
        bucket.Take(300);
        bucket.Take(100);
        clock.Now = TimeSpan.FromSeconds(2);

        var wait = bucket.Take(50);

        Assert.Equal(TimeSpan.Zero, wait);
    }

    /// <summary>
    /// Idle time never banks more than the burst.
    /// True: after a long pause only three seconds of bytes are free, and one byte more waits 10 milliseconds.
    /// False: a stream paused for an hour could then read at full speed for an hour.
    /// </summary>
    [Fact]
    public void Take_AfterALongIdle_HoldsOnlyTheBurst()
    {
        var clock = new Clock();
        var bucket = new SpeedBucket(100, () => clock.Now);
        clock.Now = TimeSpan.FromHours(1);
        Assert.Equal(TimeSpan.Zero, bucket.Take(300));

        var wait = bucket.Take(1);

        Assert.Equal(TimeSpan.FromSeconds(0.01), wait);
    }

    /// <summary>
    /// A new rate applies to the next read, with the burst the bucket already holds.
    /// True: doubling the rate halves the wait for the same debt.
    /// False: a changed speed limit would not apply to streams already open.
    /// </summary>
    [Fact]
    public void SetRate_ChangesTheWaitOfTheNextRead()
    {
        var clock = new Clock();
        var bucket = new SpeedBucket(100, () => clock.Now);
        bucket.SetRate(200);

        var wait = bucket.Take(400);

        Assert.Equal(TimeSpan.FromSeconds(0.5), wait);
    }

    /// <summary>
    /// The bucket remembers when it was last used.
    /// True: an idle bucket can be told apart from a busy one and dropped.
    /// False: buckets would pile up for streams that ended.
    /// </summary>
    [Fact]
    public void LastUsed_FollowsTheClockAtTheLastTake()
    {
        var clock = new Clock { Now = TimeSpan.FromSeconds(5) };
        var bucket = new SpeedBucket(100, () => clock.Now);
        Assert.Equal(TimeSpan.FromSeconds(5), bucket.LastUsed);
        clock.Now = TimeSpan.FromSeconds(9);

        bucket.Take(1);

        Assert.Equal(TimeSpan.FromSeconds(9), bucket.LastUsed);
    }

    /// <summary>
    /// A rate that is zero or negative, or a missing clock, is refused.
    /// True: a bad speed limit fails where it is set.
    /// False: a zero rate would divide by zero on the first throttled read.
    /// </summary>
    [Fact]
    public void Constructor_WithABadRateOrNoClock_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpeedBucket(0, () => TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpeedBucket(-5, () => TimeSpan.Zero));
        Assert.Throws<ArgumentNullException>(() => new SpeedBucket(100, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpeedBucket(100, () => TimeSpan.Zero).SetRate(0));
    }
}
