using System;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// Link health must pass over a server that did not answer for one minute and no longer.
/// </summary>
public sealed class LinkHealthTests
{
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A missing clock is refused when the service is built.
    /// True: the mistake shows at construction.
    /// False: the first failed server would throw inside a play.
    /// </summary>
    [Fact]
    public void Constructor_NullClock_Throws()
    {
        Func<DateTime> clock = null!;

        var act = () => new LinkHealth(clock);

        Assert.Throws<ArgumentNullException>(act);
    }

    /// <summary>
    /// A server nobody recorded, or no server at all, is not down.
    /// True: a server is asked until it fails.
    /// False: a link with no server key would throw or be passed over.
    /// </summary>
    /// <param name="key">The server key asked about.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("never-failed")]
    public void IsDown_NothingRecorded_IsFalse(string? key)
    {
        var health = new LinkHealth(() => _now);

        var down = health.IsDown(key);

        Assert.False(down);
    }

    /// <summary>
    /// A failed server stays down until the minute is up, and is asked again from that moment.
    /// True: plays skip a dead server's timeout for a minute, then try it again.
    /// False: every play would wait out the timeout, or a server would stay skipped for good.
    /// </summary>
    /// <param name="secondsLater">How long after the failure the server is asked about.</param>
    /// <param name="expected">Whether it is still down.</param>
    [Theory]
    [InlineData(0, true)]
    [InlineData(59, true)]
    [InlineData(60, false)]
    [InlineData(3600, false)]
    public void IsDown_AfterFailure_LastsOneMinute(int secondsLater, bool expected)
    {
        var health = new LinkHealth(() => _now);
        health.Failed("server-a");
        _now = _now.AddSeconds(secondsLater);

        var down = health.IsDown("server-a");

        Assert.Equal(expected, down);
    }

    /// <summary>
    /// An answer clears the failure at once, under any casing of the key.
    /// True: a server that came back takes plays again without waiting out the minute.
    /// False: a recovered server would be skipped, or a key in another case would miss its record.
    /// </summary>
    [Fact]
    public void Answered_AfterFailure_ClearsIt()
    {
        var health = new LinkHealth(() => _now);
        health.Failed("Server-A");
        var before = health.IsDown("server-a");

        health.Answered("SERVER-A");

        Assert.True(before);
        Assert.False(health.IsDown("server-a"));
    }

    /// <summary>
    /// A failure of one server says nothing about another.
    /// True: only the server that did not answer is passed over.
    /// False: one outage would take every server out of the running.
    /// </summary>
    [Fact]
    public void IsDown_OtherServer_IsFalse()
    {
        var health = new LinkHealth(() => _now);

        health.Failed("server-a");

        Assert.False(health.IsDown("server-b"));
    }

    /// <summary>
    /// Recording a failure or an answer for no server does nothing and does not throw.
    /// True: a link whose row lost its server key cannot break a play.
    /// False: a null key would throw inside the stream.
    /// </summary>
    [Fact]
    public void Failed_NullKey_IsHarmless()
    {
        var health = new LinkHealth(() => _now);

        health.Failed(null!);
        health.Answered(null!);

        Assert.False(health.IsDown(null));
    }
}
