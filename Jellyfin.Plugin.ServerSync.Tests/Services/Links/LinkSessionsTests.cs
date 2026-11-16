using System;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// Link sessions must hold every stream, cache download, and probe a calling server makes to the limits set for it.
/// </summary>
public sealed class LinkSessionsTests
{
    private static readonly Guid User = Guid.Parse("11111111111111111111111111111111");
    private static readonly Guid Other = Guid.Parse("22222222222222222222222222222222");
    private static readonly Guid Item = Guid.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly Guid Item2 = Guid.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A missing clock or server key is refused.
    /// True: the mistake is named where it was made.
    /// False: a stream with no server would be counted under nothing and escape its limit.
    /// </summary>
    [Fact]
    public void Members_NullArguments_Throw()
    {
        var sessions = Sessions();

        Assert.Throws<ArgumentNullException>(() => new LinkSessions(null!));
        Assert.Throws<ArgumentNullException>(() => sessions.Open(null!, User, Item));
        Assert.Throws<ArgumentNullException>(() => sessions.TryOpen(null!, User, Item, 0, 0, 0));
        Assert.Throws<ArgumentNullException>(() => sessions.TakeProbe(null!, Item, 0));
        Assert.Throws<ArgumentNullException>(() => sessions.TryOpenFill(null!));
        Assert.Throws<ArgumentNullException>(() => sessions.SpeedFor(null!, User, false, 1));
        Assert.Throws<ArgumentNullException>(() => sessions.IsActive(null!, User, Item));
        Assert.Throws<ArgumentNullException>(() => sessions.CountForPeer(null!));
    }

    /// <summary>
    /// With no limits set, every stream opens.
    /// True: 0 means no limit.
    /// False: a server left at its defaults would refuse every stream.
    /// </summary>
    [Fact]
    public void TryOpen_NoLimits_AlwaysOpens()
    {
        var sessions = Sessions();

        using var first = sessions.TryOpen("peer", User, Item, 0, 0, 100);
        using var second = sessions.TryOpen("peer", User, Item2, 0, 0, 100);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, sessions.CountForPeer("PEER"));
    }

    /// <summary>
    /// The first request of a stream says it began one, and a further request of the same stream does not. A refused request began nothing.
    /// True: the Statistics tab counts a film once however many range requests its player makes.
    /// False: every seek would count as another stream.
    /// </summary>
    [Fact]
    public void TryOpen_SaysWhetherTheRequestBeganAStream()
    {
        var sessions = Sessions();

        using var first = sessions.TryOpen("peer", User, Item, 1, 0, 0, out var began);
        using var again = sessions.TryOpen("peer", User, Item, 1, 0, 0, out var beganAgain);
        var refused = sessions.TryOpen("peer", User, Item2, 1, 0, 0, out var beganRefused);
        using var other = sessions.Open("other", User, Item, out var beganOther);

        Assert.True(began);
        Assert.False(beganAgain);
        Assert.Null(refused);
        Assert.False(beganRefused);
        Assert.True(beganOther);
    }

    /// <summary>
    /// A server at its limit is refused a new stream, while a further request of a running stream still opens.
    /// True: the limit counts streams, so seeking or buffering never trips it.
    /// False: a player's second range request would be refused mid film.
    /// </summary>
    [Fact]
    public void TryOpen_ServerAtItsLimit_RefusesOnlyNewStreams()
    {
        var sessions = Sessions();
        using var running = sessions.TryOpen("peer", User, Item, 1, 0, 0);

        using var another = sessions.TryOpen("PEER", Other, Item2, 1, 0, 0);
        using var sameAgain = sessions.TryOpen("Peer", User, Item, 1, 0, 0);
        using var otherServer = sessions.TryOpen("peer-2", Other, Item2, 1, 0, 0);

        Assert.NotNull(running);
        Assert.Null(another);
        Assert.NotNull(sameAgain);
        Assert.NotNull(otherServer);
    }

    /// <summary>
    /// A user's limit counts their link streams through every server plus what they play elsewhere.
    /// True: a user cannot get around their limit by coming in through a second server.
    /// False: each server would give the user a fresh allowance.
    /// </summary>
    /// <param name="elsewhere">The streams the user holds outside links.</param>
    /// <param name="max">The user's limit.</param>
    /// <param name="expected">Whether a new stream opens.</param>
    [Theory]
    [InlineData(0, 2, true)]
    [InlineData(1, 2, false)]
    [InlineData(0, 1, false)]
    [InlineData(5, 0, true)]
    public void TryOpen_UserLimit_CountsEveryServerAndElsewhere(int elsewhere, int max, bool expected)
    {
        var sessions = Sessions();
        using var running = sessions.Open("peer-1", User, Item);

        using var opened = sessions.TryOpen("peer-2", User, Item2, 0, max, elsewhere);

        Assert.Equal(expected, opened is not null);
    }

    /// <summary>
    /// A closed stream holds its slot for the linger and frees it after.
    /// True: a gap between a player's requests does not hand its slot to someone else, and a finished one does.
    /// False: a buffering viewer would lose their stream, or a slot would never come back.
    /// </summary>
    /// <param name="secondsLater">How long after the stream's last request ended.</param>
    /// <param name="expected">Whether another stream opens.</param>
    [Theory]
    [InlineData(90, false)]
    [InlineData(91, true)]
    public void TryOpen_AfterAStreamCloses_WaitsOutTheLinger(int secondsLater, bool expected)
    {
        var sessions = Sessions();
        sessions.Open("peer", User, Item).Dispose();
        _now = _now.AddSeconds(secondsLater);

        using var opened = sessions.TryOpen("peer", Other, Item2, 1, 0, 0);

        Assert.Equal(expected, opened is not null);
        Assert.Equal(!expected, sessions.IsActive("peer", User, Item));
    }

    /// <summary>
    /// A stream with a request still open is never dropped, however long it runs.
    /// True: a whole film read in one request stays counted.
    /// False: a long request would fall out of the limits after 90 seconds.
    /// </summary>
    [Fact]
    public void CountForUser_OpenRequest_NeverLapses()
    {
        var sessions = Sessions();
        using var open = sessions.Open("peer", User, Item);
        sessions.Open("peer", User, Item).Dispose();

        _now = _now.AddHours(5);

        Assert.Equal(1, sessions.CountForUser(User));
        Assert.Equal(0, sessions.CountForUser(Other));
    }

    /// <summary>
    /// A probe is served until the server has spent its allowance on the file, and asking with 0 bytes spends nothing.
    /// True: a probe reads headers and index, and never the whole file.
    /// False: the probe flag would copy whole films past every limit.
    /// </summary>
    [Fact]
    public void TakeProbe_PastTheAllowance_IsRefused()
    {
        var sessions = Sessions();

        var asking = sessions.TakeProbe("peer", Item, 0);
        var most = sessions.TakeProbe("peer", Item, int.MaxValue);
        var stillUnder = sessions.TakeProbe("peer", Item, 1);
        var over = sessions.TakeProbe("PEER", Item, 0);

        Assert.Equal(512L * 1024 * 1024, LinkSessions.ProbeAllowance);
        Assert.True(asking);
        Assert.True(most);
        Assert.False(stillUnder);
        Assert.False(over);
    }

    /// <summary>
    /// The allowance is per server and file, and starts again after an hour.
    /// True: one probed file does not stop another being probed, and a later rescan can probe again.
    /// False: a server would be locked out of probing after its first large file.
    /// </summary>
    [Fact]
    public void TakeProbe_OtherFileOrNextHour_HasAFreshAllowance()
    {
        var sessions = Sessions();
        sessions.TakeProbe("peer", Item, int.MaxValue);
        var spent = sessions.TakeProbe("peer", Item, 0);

        var otherFile = sessions.TakeProbe("peer", Item2, 0);
        var otherServer = sessions.TakeProbe("peer-2", Item, 0);
        _now = _now.AddHours(1);
        var atTheHour = sessions.TakeProbe("peer", Item, 0);
        _now = _now.AddSeconds(1);
        var afterTheHour = sessions.TakeProbe("peer", Item, 0);

        Assert.False(spent);
        Assert.True(otherFile);
        Assert.True(otherServer);
        Assert.False(atTheHour);
        Assert.True(afterTheHour);
    }

    /// <summary>
    /// A server holds at most two cache downloads at once, and ending one frees one place.
    /// True: the download flag cannot be used to stream uncounted.
    /// False: a server could open any number of unlimited downloads.
    /// </summary>
    [Fact]
    public void TryOpenFill_AtMostTwoPerServer()
    {
        var sessions = Sessions();
        var first = sessions.TryOpenFill("peer");
        using var second = sessions.TryOpenFill("PEER");

        using var third = sessions.TryOpenFill("peer");
        using var otherServer = sessions.TryOpenFill("peer-2");
        first!.Dispose();
        first.Dispose();
        using var afterOneEnded = sessions.TryOpenFill("peer");
        using var stillFull = sessions.TryOpenFill("peer");

        Assert.Equal(2, LinkSessions.MaxFillsPerPeer);
        Assert.NotNull(second);
        Assert.Null(third);
        Assert.NotNull(otherServer);
        Assert.NotNull(afterOneEnded);
        Assert.Null(stillFull);
    }

    /// <summary>
    /// Every request for one server and user shares one speed, and a cache download or another user has its own.
    /// True: a speed limit holds for the user as a whole, not for each request.
    /// False: a player opening four connections would get four times the speed.
    /// </summary>
    [Fact]
    public void SpeedFor_SameServerAndUser_SharesOneBucket()
    {
        var sessions = Sessions();
        var stream = sessions.SpeedFor("peer", User, false, 1000);

        var sameStream = sessions.SpeedFor("PEER", User, false, 2000);
        var fill = sessions.SpeedFor("peer", User, true, 1000);
        var otherUser = sessions.SpeedFor("peer", Other, false, 1000);
        var noUser = sessions.SpeedFor("peer", null, false, 1000);

        Assert.Same(stream, sameStream);
        Assert.NotSame(stream, fill);
        Assert.NotSame(stream, otherUser);
        Assert.NotSame(stream, noUser);
        Assert.Same(noUser, sessions.SpeedFor("peer", null, false, 1000));
    }

    private LinkSessions Sessions() => new(() => _now);
}
