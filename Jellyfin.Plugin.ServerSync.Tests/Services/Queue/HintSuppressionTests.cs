using System;
using System.Globalization;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// An object a peer declined is held back from that peer alone, for a day or until the peer's settings change, and the list stays bounded.
/// </summary>
public sealed class HintSuppressionTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Only an answer that will repeat until the peer's settings change holds an object back.
    /// True: an unmapped or filtered object is not sent again, and one that may still apply is.
    /// False: a peer is sent the same declined hint forever, or a hint that would apply is withheld.
    /// </summary>
    [Theory]
    [InlineData(HintCode.NotMapped, true)]
    [InlineData(HintCode.Filtered, true)]
    [InlineData(HintCode.Ignored, true)]
    [InlineData(HintCode.ModuleOff, true)]
    [InlineData(HintCode.Unsupported, true)]
    [InlineData(HintCode.Priority, true)]
    [InlineData(HintCode.Queued, false)]
    [InlineData(HintCode.NoLocalItem, false)]
    [InlineData(HintCode.AlreadyCurrent, false)]
    [InlineData(HintCode.Invalid, false)]
    [InlineData(HintCode.FutureVersion, false)]
    [InlineData(HintCode.Applied, false)]
    [InlineData(HintCode.Unchanged, false)]
    [InlineData(HintCode.Superseded, false)]
    [InlineData(HintCode.Locked, false)]
    [InlineData(HintCode.AwaitingApproval, false)]
    [InlineData(HintCode.SourceGone, false)]
    [InlineData(HintCode.Unreadable, false)]
    [InlineData(HintCode.GaveUp, false)]
    public void Suppresses_EachCode_HoldsOnlyAnswersThatRepeat(HintCode code, bool suppresses)
    {
        var result = HintSuppression.Suppresses(code);

        Assert.Equal(suppresses, result);
    }

    /// <summary>
    /// A declined object is noted, counted, and held.
    /// True: the sender stops queueing hints the peer will only decline again.
    /// False: the note is lost and every edit is sent and declined.
    /// </summary>
    [Fact]
    public void Note_DeclinedObject_IsHeldAndCounted()
    {
        var suppression = new HintSuppression();

        var noted = suppression.Note("peer", HintKind.Metadata, "item", HintCode.NotMapped, "rev", Now);

        Assert.True(noted);
        Assert.Equal(1, suppression.Count);
        Assert.True(suppression.IsHeld("peer", HintKind.Metadata, "item", "rev", Now));
    }

    /// <summary>
    /// A note with no peer or no key holds nothing.
    /// True: a malformed row never becomes an entry that matches other objects.
    /// False: an empty key is held and blocks whatever shares its prefix.
    /// </summary>
    [Theory]
    [InlineData("", "item")]
    [InlineData(null, "item")]
    [InlineData("peer", "")]
    [InlineData("peer", null)]
    public void Note_MissingPeerOrKey_HoldsNothing(string? peerKey, string? key)
    {
        var suppression = new HintSuppression();

        var noted = suppression.Note(peerKey!, HintKind.Metadata, key!, HintCode.NotMapped, "rev", Now);

        Assert.False(noted);
        Assert.Equal(0, suppression.Count);
    }

    /// <summary>
    /// A hold lasts up to, and not including, the moment a day after it was noted.
    /// True: a declined object is offered again the next day in case the peer changed.
    /// False: the hold ends early and floods the peer, or never ends.
    /// </summary>
    [Fact]
    public void IsHeld_AtTheEndOfTheHold_IsReleasedAndForgotten()
    {
        var suppression = new HintSuppression();
        suppression.Note("peer", HintKind.Metadata, "item", HintCode.Filtered, "rev", Now);

        var justBefore = suppression.IsHeld("peer", HintKind.Metadata, "item", "rev", Now + HintSuppression.Hold - TimeSpan.FromTicks(1));
        var atTheEnd = suppression.IsHeld("peer", HintKind.Metadata, "item", "rev", Now + HintSuppression.Hold);

        Assert.True(justBefore);
        Assert.False(atTheEnd);
        Assert.Equal(0, suppression.Count);
    }

    /// <summary>
    /// A hold noted without a revision matches only a peer whose revision is still unknown.
    /// True: a peer that never said what it wants keeps its holds until it does.
    /// False: a hold from an unknown revision outlives the peer's first real answer.
    /// </summary>
    [Fact]
    public void IsHeld_NoRevisionKnown_MatchesOnlyNoRevision()
    {
        var suppression = new HintSuppression();
        suppression.Note("peer", HintKind.Metadata, "item", HintCode.NotMapped, null, Now);

        var stillUnknown = suppression.IsHeld("peer", HintKind.Metadata, "item", null, Now);
        var nowKnown = suppression.IsHeld("peer", HintKind.Metadata, "item", "rev", Now);

        Assert.True(stillUnknown);
        Assert.False(nowKnown);
    }

    /// <summary>
    /// A hold covers one sync type of one object.
    /// True: a peer that filters an item's file still receives its metadata.
    /// False: declining one kind silences every kind for that key.
    /// </summary>
    [Fact]
    public void IsHeld_AnotherKindOfTheSameKey_IsNotHeld()
    {
        var suppression = new HintSuppression();
        suppression.Note("peer", HintKind.Content, "item", HintCode.Filtered, "rev", Now);

        var held = suppression.IsHeld("peer", HintKind.Metadata, "item", "rev", Now);

        Assert.False(held);
    }

    /// <summary>
    /// An object never noted is not held.
    /// True: a new object is sent to every peer.
    /// False: an empty list would block everything.
    /// </summary>
    [Fact]
    public void IsHeld_NothingNoted_IsNotHeld()
    {
        var suppression = new HintSuppression();

        var held = suppression.IsHeld("peer", HintKind.Metadata, "item", "rev", Now);

        Assert.False(held);
    }

    /// <summary>
    /// Clearing forgets every held object.
    /// True: the operator's reset sends everything again.
    /// False: holds survive a reset and changes stay unsent for a day.
    /// </summary>
    [Fact]
    public void Clear_WithHeldObjects_ForgetsThemAll()
    {
        var suppression = new HintSuppression();
        suppression.Note("peer-a", HintKind.Metadata, "one", HintCode.NotMapped, "rev", Now);
        suppression.Note("peer-b", HintKind.People, "Jane Doe", HintCode.ModuleOff, "rev", Now);

        suppression.Clear();

        Assert.Equal(0, suppression.Count);
        Assert.False(suppression.IsHeld("peer-a", HintKind.Metadata, "one", "rev", Now));
    }

    /// <summary>
    /// Clearing one peer leaves the others, even one whose key starts the same way, and matches the key in any case.
    /// True: a peer whose settings changed gets its objects again without disturbing the rest.
    /// False: "peer" also clears "peer-2", or a differently cased key clears nothing.
    /// </summary>
    [Fact]
    public void ClearPeer_OnePeer_LeavesPeersWithALongerKey()
    {
        var suppression = new HintSuppression();
        suppression.Note("peer", HintKind.Metadata, "one", HintCode.NotMapped, "rev", Now);
        suppression.Note("peer", HintKind.Metadata, "two", HintCode.NotMapped, "rev", Now);
        suppression.Note("peer-2", HintKind.Metadata, "one", HintCode.NotMapped, "rev", Now);

        var cleared = suppression.ClearPeer("PEER");

        Assert.Equal(2, cleared);
        Assert.Equal(1, suppression.Count);
        Assert.True(suppression.IsHeld("peer-2", HintKind.Metadata, "one", "rev", Now));
    }

    /// <summary>
    /// Clearing a peer with nothing held removes nothing.
    /// True: a first contact with a peer costs nothing.
    /// False: the count reported to the log is wrong.
    /// </summary>
    [Fact]
    public void ClearPeer_NothingHeld_ReturnsZero()
    {
        var suppression = new HintSuppression();

        var cleared = suppression.ClearPeer("peer");

        Assert.Equal(0, cleared);
    }

    /// <summary>
    /// At the cap, a new note first drops the holds that have run out.
    /// True: live holds survive a full list.
    /// False: a full list throws away holds that still apply, and declined hints are sent again.
    /// </summary>
    [Fact]
    public void Note_AtTheCapWithExpiredHolds_DropsOnlyTheExpired()
    {
        var suppression = new HintSuppression();
        var old = Now - HintSuppression.Hold;
        for (var i = 0; i < HintSuppression.MaxHeld - 1; i++)
        {
            suppression.Note("peer", HintKind.Metadata, "old" + i.ToString(CultureInfo.InvariantCulture), HintCode.NotMapped, "rev", old);
        }

        suppression.Note("peer", HintKind.Metadata, "live", HintCode.NotMapped, "rev", Now);

        suppression.Note("peer", HintKind.Metadata, "new", HintCode.NotMapped, "rev", Now);

        Assert.Equal(2, suppression.Count);
        Assert.True(suppression.IsHeld("peer", HintKind.Metadata, "live", "rev", Now));
        Assert.True(suppression.IsHeld("peer", HintKind.Metadata, "new", "rev", Now));
    }

    /// <summary>
    /// At the cap with nothing expired, the list starts over with the new note.
    /// True: the list never grows past its cap, whatever a peer declines.
    /// False: a peer that declines everything grows this server's memory without bound.
    /// </summary>
    [Fact]
    public void Note_AtTheCapWithNothingExpired_StartsOver()
    {
        var suppression = new HintSuppression();
        for (var i = 0; i < HintSuppression.MaxHeld; i++)
        {
            suppression.Note("peer", HintKind.Metadata, "live" + i.ToString(CultureInfo.InvariantCulture), HintCode.NotMapped, "rev", Now);
        }

        suppression.Note("peer", HintKind.Metadata, "new", HintCode.NotMapped, "rev", Now);

        Assert.Equal(1, suppression.Count);
        Assert.True(suppression.IsHeld("peer", HintKind.Metadata, "new", "rev", Now));
    }
}
