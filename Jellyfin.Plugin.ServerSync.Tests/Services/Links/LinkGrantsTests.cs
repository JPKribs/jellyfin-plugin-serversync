using System;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// Link grants must let only a user the source allowed, or this server's bounded probe, read a link, and only for a while.
/// </summary>
public sealed class LinkGrantsTests
{
    private const string Link = "aaaa";
    private static readonly Guid User = Guid.Parse("11111111111111111111111111111111");
    private static readonly Guid Other = Guid.Parse("22222222222222222222222222222222");
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A missing clock or link id is refused.
    /// True: the mistake is named where it was made.
    /// False: a null id would be stored and match nothing, or throw inside a stream.
    /// </summary>
    [Fact]
    public void Members_NullArguments_Throw()
    {
        var grants = Grants();

        Assert.Throws<ArgumentNullException>(() => new LinkGrants(null!));
        Assert.Throws<ArgumentNullException>(() => grants.Grant(null!, User));
        Assert.Throws<ArgumentNullException>(() => grants.Use(null!));
        Assert.Throws<ArgumentNullException>(() => grants.Refresh(null!, User));
        Assert.Throws<ArgumentNullException>(() => grants.GrantProbe(null!));
        Assert.Throws<ArgumentNullException>(() => grants.SpendProbe(null!, 1));
        Assert.Throws<ArgumentNullException>(() => grants.RouteFor(null!, User));
        Assert.Throws<ArgumentNullException>(() => grants.Reroute(Link, User, null!));
        Assert.Throws<ArgumentNullException>(() => grants.Hold(null!, User));
    }

    /// <summary>
    /// A link nobody was granted cannot be used, refreshed, or spent against.
    /// True: a request for a link the source never approved reads nothing.
    /// False: anyone who learned a link's URL could stream it.
    /// </summary>
    [Fact]
    public void Use_NoGrant_IsNull()
    {
        var grants = Grants();

        var grant = grants.Use(Link);

        Assert.Null(grant);
        Assert.False(grants.Refresh(Link, User));
        Assert.False(grants.SpendProbe(Link, 1));
        Assert.Null(grants.RouteFor(Link, User));
    }

    /// <summary>
    /// A grant never used lapses two minutes after it was made, to the second.
    /// True: an approval the player never acted on stops opening the link.
    /// False: one press of play would leave the link readable for good.
    /// </summary>
    /// <param name="secondsLater">How long after the grant the stream asks.</param>
    /// <param name="expected">Whether the grant still holds.</param>
    [Theory]
    [InlineData(119, true)]
    [InlineData(120, false)]
    public void Refresh_UnusedGrant_LapsesAfterTwoMinutes(int secondsLater, bool expected)
    {
        var grants = Grants();
        grants.Grant(Link, User);
        _now = _now.AddSeconds(secondsLater);

        var live = grants.Refresh(Link, User);

        Assert.Equal(expected, live);
    }

    /// <summary>
    /// A link has a viewer only while a user holds a live grant on it.
    /// True: a request with no user is let through only during somebody's play.
    /// False: a lapsed play, or this server's own probe, would open a link's subtitles to anyone.
    /// </summary>
    /// <param name="user">Whether a user was granted.</param>
    /// <param name="probe">Whether the probe was granted.</param>
    /// <param name="secondsLater">How long after the grants the question is asked.</param>
    /// <param name="expected">Whether the link has a viewer.</param>
    [Theory]
    [InlineData(true, false, 119, true)]
    [InlineData(true, false, 120, false)]
    [InlineData(false, true, 0, false)]
    [InlineData(false, false, 0, false)]
    public void HasViewer_OnlyALiveUserGrant_Counts(bool user, bool probe, int secondsLater, bool expected)
    {
        var grants = Grants();
        if (user)
        {
            grants.Grant(Link, User);
        }

        if (probe)
        {
            grants.GrantProbe(Link);
        }

        _now = _now.AddSeconds(secondsLater);

        Assert.Equal(expected, grants.HasViewer(Link));
    }

    /// <summary>
    /// Asking whether a link has a viewer does not use the grant.
    /// True: requests with no user cannot keep a play's grant alive past its window.
    /// False: a player polling for subtitles would hold the link open for good.
    /// </summary>
    [Fact]
    public void HasViewer_Asked_DoesNotExtendTheGrant()
    {
        var grants = Grants();
        grants.Grant(Link, User);
        _now = _now.AddSeconds(100);
        Assert.True(grants.HasViewer(Link));
        _now = _now.AddSeconds(20);

        Assert.False(grants.HasViewer(Link));
    }

    /// <summary>
    /// A grant given again while its play is under way stays in use.
    /// True: a second PlaybackInfo during a film does not put the play back on the two minute window.
    /// False: a paused viewer whose client asked again would be cut off two minutes later.
    /// </summary>
    [Fact]
    public void Grant_AgainWhileInUse_KeepsTheLongWindow()
    {
        var grants = Grants();
        grants.Grant(Link, User);
        grants.Refresh(Link, User);

        grants.Grant(Link, User, new LinkRoute("b", "bbbb"));
        _now = _now.AddMinutes(30);

        Assert.True(grants.Refresh(Link, User));
        Assert.Equal(new LinkRoute("b", "bbbb"), grants.RouteFor(Link, User));
    }

    /// <summary>
    /// Using a link picks the user seen most recently and names them, and a link id matches in any case.
    /// True: ffmpeg's tokenless read runs as the user who just pressed play.
    /// False: a read would run as an earlier viewer with other rights.
    /// </summary>
    [Fact]
    public void Use_TwoUsers_TakesTheMostRecent()
    {
        var grants = Grants();
        grants.Grant(Link, User);
        _now = _now.AddSeconds(10);
        grants.Grant(Link, Other, new LinkRoute("b", "bbbb"));

        var grant = grants.Use(Link.ToUpperInvariant());

        Assert.NotNull(grant);
        Assert.Equal(Other, grant.UserId);
        Assert.False(grant.Probe);
        Assert.Equal(new LinkRoute("b", "bbbb"), grant.Route);
    }

    /// <summary>
    /// A user's grant is used ahead of the probe's, however new the probe's is.
    /// True: a play is counted and limited as its user even while a probe is waiting.
    /// False: a play would ride the probe's grant, uncounted and unnamed.
    /// </summary>
    [Fact]
    public void Use_UserAndProbe_TakesTheUser()
    {
        var grants = Grants();
        grants.GrantProbe(Link);
        _now = _now.AddSeconds(-30);
        grants.Grant(Link, User);
        _now = _now.AddSeconds(30);

        var grant = grants.Use(Link);

        Assert.Equal(User, grant!.UserId);
        Assert.False(grant.Probe);
    }

    /// <summary>
    /// The probe is given no grant while a user holds a live one.
    /// True: once the user's grant lapses nothing is left to read the link.
    /// False: a probe grant made during a play would keep the link open for an hour after it.
    /// </summary>
    [Fact]
    public void GrantProbe_WhileAUserHoldsAGrant_AddsNothing()
    {
        var grants = Grants();
        grants.Grant(Link, User);

        grants.GrantProbe(Link);
        _now = _now.Add(LinkGrants.Unused);

        Assert.Null(grants.Use(Link));
    }

    /// <summary>
    /// A probe grant names no user, and lasts an hour from when it was made however often it is used.
    /// True: the refresh queue can reach a new link long after it was written, but not for ever.
    /// False: each probe read would push the window out and keep the link open without end.
    /// </summary>
    /// <param name="minutesLater">How long after the grant the link is read.</param>
    /// <param name="expected">Whether the read is allowed.</param>
    [Theory]
    [InlineData(59, true)]
    [InlineData(60, false)]
    public void Use_ProbeGrant_KeepsItsWindowFromTheStart(int minutesLater, bool expected)
    {
        var grants = Grants();
        grants.GrantProbe(Link);
        _now = _now.AddMinutes(30);
        var midway = grants.Use(Link);
        _now = _now.AddMinutes(minutesLater - 30);

        var grant = grants.Use(Link);

        Assert.True(midway!.Probe);
        Assert.Null(midway.UserId);
        Assert.Equal(expected, grant is not null);
    }

    /// <summary>
    /// A probe grant ends once it has read its budget.
    /// True: nothing reads a whole film under the probe's grant.
    /// False: the probe flag would be a way to copy files with no user and no limit.
    /// </summary>
    [Fact]
    public void SpendProbe_PastTheBudget_EndsTheGrant()
    {
        var grants = Grants();
        grants.GrantProbe(Link);

        var underBudget = grants.SpendProbe(Link, LinkGrants.ProbeBudget - 1);
        var lastByte = grants.SpendProbe(Link, 1);
        var afterBudget = grants.SpendProbe(Link, 1);

        Assert.True(underBudget);
        Assert.True(lastByte);
        Assert.False(afterBudget);
        Assert.Null(grants.Use(Link));
    }

    /// <summary>
    /// A grant that lapsed is not brought back by moving it to another server.
    /// True: a failover long after a play ended opens nothing.
    /// False: a stale stream could revive its own approval.
    /// </summary>
    [Fact]
    public void Reroute_LapsedGrant_StaysLapsed()
    {
        var grants = Grants();
        grants.Grant(Link, User);
        _now = _now.Add(LinkGrants.Unused);

        grants.Reroute(Link, User, new LinkRoute("b", "bbbb"));

        Assert.Null(grants.RouteFor(Link, User));
        Assert.False(grants.Refresh(Link, User));
    }

    /// <summary>
    /// Only a play that was used and has a route counts as streaming: not a grant never used, a probe, or one with no route.
    /// True: this server's stream limits count real plays.
    /// False: a press of play that never started, or a probe, would use up a stream slot.
    /// </summary>
    [Fact]
    public void Streaming_CountsOnlyUsedRoutedPlays()
    {
        var grants = Grants();
        grants.Grant("unused", User, new LinkRoute("a", "1"));
        grants.GrantProbe("probed");
        grants.Use("probed");
        grants.Grant("routeless", User);
        grants.Refresh("routeless", User);
        grants.Grant("playing", User, new LinkRoute("a", "2"));
        grants.Refresh("playing", User);

        var streaming = grants.Streaming(null);

        Assert.Equal(1, streaming);
    }

    /// <summary>
    /// The count is taken for one server in any case of its key, and leaves out the play being chosen for.
    /// True: a play never counts against itself, and each server's limit sees only its own plays.
    /// False: a seek would be refused by the limit its own play fills.
    /// </summary>
    [Fact]
    public void Streaming_ByServer_LeavesOutTheChosenPlay()
    {
        var grants = Grants();
        Play(grants, "one", User, "Server-A");
        Play(grants, "two", Other, "server-b");

        Assert.Equal(2, grants.Streaming(null));
        Assert.Equal(1, grants.Streaming("SERVER-A"));
        Assert.Equal(0, grants.Streaming("server-a", "ONE", User));
        Assert.Equal(1, grants.Streaming("server-a", "one", Other));
        Assert.Equal(0, grants.Streaming("server-c"));
    }

    /// <summary>
    /// A play stops counting 90 seconds after its last request, unless a request is still open.
    /// True: a player reading a whole film in one request stays counted, and a finished play frees its slot.
    /// False: long requests would drop out of the count, or ended plays would hold their slot for an hour.
    /// </summary>
    [Fact]
    public void Streaming_Hold_CountsWhileOpenAndLingers()
    {
        var grants = Grants();
        Play(grants, Link, User, "a");
        var hold = grants.Hold(Link, User);
        _now = _now.AddMinutes(30);
        var whileOpen = grants.Streaming("a");

        hold.Dispose();
        hold.Dispose();
        _now = _now.AddSeconds(89);
        var lingering = grants.Streaming("a");
        _now = _now.AddSeconds(1);
        var ended = grants.Streaming("a");

        Assert.Equal(1, whileOpen);
        Assert.Equal(1, lingering);
        Assert.Equal(0, ended);
    }

    /// <summary>
    /// Disposing one hold twice closes one request, so a second open request still counts.
    /// True: a double dispose cannot hide a stream that is still running.
    /// False: a play with a request open would vanish from the limits.
    /// </summary>
    [Fact]
    public void Hold_DisposedTwice_ClosesOneRequest()
    {
        var grants = Grants();
        Play(grants, Link, User, "a");
        var first = grants.Hold(Link, User);
        using var second = grants.Hold(Link, User);

        first.Dispose();
        first.Dispose();
        _now = _now.AddMinutes(30);

        Assert.Equal(1, grants.Streaming("a"));
    }

    private static void Play(LinkGrants grants, string linkId, Guid user, string serverKey)
    {
        grants.Grant(linkId, user, new LinkRoute(serverKey, linkId));
        grants.Refresh(linkId, user);
    }

    private LinkGrants Grants() => new(() => _now);
}
