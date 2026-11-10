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
    /// Reads inside the opening burst are not held back, and reads past it wait until the rate allows them.
    /// True: a player fills its first buffer at once, and a long read settles at the rate.
    /// False: the limit would either never apply or slow the start of every play.
    /// </summary>
    [Fact]
    public void DelayFor_LetsABurstThrough_ThenHoldsTheRate()
    {
        const long Rate = 1000;

        Assert.Equal(TimeSpan.Zero, ThrottledReadStream.DelayFor(0, TimeSpan.Zero, Rate));
        Assert.Equal(TimeSpan.Zero, ThrottledReadStream.DelayFor((long)(Rate * ThrottledReadStream.BurstSeconds), TimeSpan.Zero, Rate));
        Assert.Equal(TimeSpan.FromSeconds(2), ThrottledReadStream.DelayFor((long)(Rate * (ThrottledReadStream.BurstSeconds + 2)), TimeSpan.Zero, Rate));
        Assert.Equal(TimeSpan.Zero, ThrottledReadStream.DelayFor((long)(Rate * (ThrottledReadStream.BurstSeconds + 2)), TimeSpan.FromSeconds(5), Rate));
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
        await using var stream = new ThrottledReadStream(new MemoryStream(data), 1_000_000);

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
