using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Utilities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

/// <summary>
/// The speed limit on link streams a source sends: the rate itself, and which rate applies when.
/// </summary>
public class ThrottledReadStreamTests
{
    /// <summary>
    /// Bytes inside the opening burst are not held back, bytes past it wait until the rate has made
    /// them up, and time spent idle earns at most one burst.
    /// True: a player fills its first buffer at once, a long read settles at the rate, and a pause does not bank unlimited speed.
    /// False: the limit would either never apply or slow the start of every play.
    /// </summary>
    [Fact]
    public void Bucket_LetsABurstThrough_ThenHoldsTheRate()
    {
        const int Rate = 1000;
        var now = TimeSpan.Zero;
        var bucket = new SpeedBucket(Rate, () => now);
        var burst = (int)(Rate * SpeedBucket.BurstSeconds);

        Assert.Equal(TimeSpan.Zero, bucket.Take(burst));
        Assert.Equal(TimeSpan.FromSeconds(2), bucket.Take(2 * Rate));

        now += TimeSpan.FromSeconds(2);
        Assert.Equal(TimeSpan.FromSeconds(1), bucket.Take(Rate));

        now += TimeSpan.FromHours(1);
        Assert.Equal(TimeSpan.Zero, bucket.Take(burst));
        Assert.Equal(TimeSpan.FromSeconds(1), bucket.Take(Rate));
    }

    /// <summary>
    /// A bucket on the real clock earns bytes back as time passes.
    /// True: a stream held to a speed keeps moving at that speed.
    /// False: once the opening burst was spent the waits would grow without end, and the stream would stall.
    /// </summary>
    [Fact]
    public async Task Bucket_OnTheRealClock_RefillsWithTime()
    {
        const int Rate = 1_000_000;
        var bucket = new SpeedBucket(Rate);
        var burst = (int)(Rate * SpeedBucket.BurstSeconds);

        Assert.Equal(TimeSpan.Zero, bucket.Take(burst));
        var owed = bucket.Take(Rate / 10);
        Assert.InRange(owed, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(110));

        await Task.Delay(TimeSpan.FromMilliseconds(400));

        Assert.Equal(TimeSpan.Zero, bucket.Take(Rate / 10));
    }

    /// <summary>
    /// Two requests that share a bucket share its burst and its rate.
    /// True: a player that splits its reading over several requests gets the limit once, not once for each.
    /// False: opening more requests, or asking in small ranges, would get around the limit.
    /// </summary>
    [Fact]
    public void Bucket_IsSharedAcrossRequests()
    {
        const int Rate = 1000;
        var now = TimeSpan.Zero;
        var bucket = new SpeedBucket(Rate, () => now);
        var burst = (int)(Rate * SpeedBucket.BurstSeconds);

        Assert.Equal(TimeSpan.Zero, bucket.Take(burst));
        // A second request arrives at once. It gets no burst of its own.
        Assert.Equal(TimeSpan.FromSeconds(1), bucket.Take(Rate));
        Assert.Equal(TimeSpan.FromSeconds(2), bucket.Take(Rate));
    }

    /// <summary>
    /// The wrapper reads the same bytes as the file, and stays seekable for byte ranges.
    /// True: a limited stream still serves seeks and sends the file unchanged.
    /// False: a speed limit would break seeking or corrupt what is sent.
    /// </summary>
    [Fact]
    public async Task Stream_ReadsTheSameBytes_AndSeeks()
    {
        var data = new byte[5000];
        Random.Shared.NextBytes(data);
        await using var stream = new ThrottledReadStream(new MemoryStream(data), new SpeedBucket(1_000_000));

        Assert.True(stream.CanSeek);
        Assert.Equal(data.Length, stream.Length);
        stream.Seek(1000, SeekOrigin.Begin);
        var read = new byte[4000];
        await stream.ReadExactlyAsync(read);

        Assert.Equal(data[1000..], read);
    }

    /// <summary>
    /// A server entry's link stream speed is in megabits, and the scheduled speed takes over inside the
    /// bandwidth schedule's hours.
    /// True: the source sends at the rate the administrator set for the hour it is.
    /// False: the wrong rate, or none, would apply.
    /// </summary>
    [Fact]
    public void Speed_FollowsTheSchedule()
    {
        var server = new SourceServer { LinkStreamSpeedMbps = 80, LinkScheduledStreamSpeedMbps = 8 };
        var config = new PluginConfiguration { EnableBandwidthScheduling = true, ScheduledStartHour = 18, ScheduledEndHour = 23 };

        Assert.Equal(10_000_000, server.GetLinkStreamSpeedBytes(config.IsInBandwidthSchedule(12)));
        Assert.Equal(1_000_000, server.GetLinkStreamSpeedBytes(config.IsInBandwidthSchedule(20)));

        config.EnableBandwidthScheduling = false;
        Assert.Equal(10_000_000, server.GetLinkStreamSpeedBytes(config.IsInBandwidthSchedule(20)));
        Assert.Equal(0, new SourceServer().GetLinkStreamSpeedBytes(false));
    }
}
