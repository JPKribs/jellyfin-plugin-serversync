using System;
using Jellyfin.Plugin.ServerSync.Utilities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

public class UtcTimeTests
{
    /// <summary>
    /// A time with no zone is read as UTC, a local time is converted, and both format the same way.
    /// True: every stored time is UTC text ending in Z, so SQL compares them in time order.
    /// False: a time with no zone shifts by the server's offset, or sorts out of order against UTC text.
    /// </summary>
    [Fact]
    public void Format_WritesUtcWithZ()
    {
        var utc = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("2026-10-04T12:00:00.0000000Z", UtcTime.Format(utc));
        Assert.Equal("2026-10-04T12:00:00.0000000Z", UtcTime.Format(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified)));
        Assert.Equal("2026-10-04T12:00:00.0000000Z", UtcTime.Format(utc.ToLocalTime()));
    }

    /// <summary>
    /// Stored text parses to UTC whatever form it was written in, and text that is not a time is reported.
    /// True: a row written by an older build without a zone reads back as the same instant.
    /// False: old rows read back hours off, and a row's age is miscounted.
    /// </summary>
    [Theory]
    [InlineData("2026-10-04T12:00:00.0000000Z")]
    [InlineData("2026-10-04T12:00:00.0000000")]
    [InlineData("2026-10-04T14:00:00.0000000+02:00")]
    public void Parse_ReadsEveryStoredFormAsUtc(string text)
    {
        Assert.True(UtcTime.TryParse(text, out var parsed));
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        Assert.Equal(new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc), parsed);
        Assert.False(UtcTime.TryParse("not a time", out var bad));
        Assert.Equal(DateTime.MinValue, bad);
    }
}
