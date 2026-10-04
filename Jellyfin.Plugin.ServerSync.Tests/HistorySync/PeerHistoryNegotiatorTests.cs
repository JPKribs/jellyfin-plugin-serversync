using System;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.HistorySync;

/// <summary>
/// Tests for the compare and set rule the peer history endpoint applies before writing.
/// </summary>
public class PeerHistoryNegotiatorTests
{
    private static PeerHistoryState State(bool played, int count, long position, DateTime? last, bool favorite) => new()
    {
        Played = played,
        PlayCount = count,
        PlaybackPositionTicks = position,
        LastPlayedDate = last,
        IsFavorite = favorite
    };

    private static readonly DateTime Noon = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The receiver already holds the proposal, so nothing is written.
    /// </summary>
    [Fact]
    public void Decide_CurrentEqualsProposed_IsUnchanged()
    {
        var current = State(true, 1, 0, Noon, false);
        var proposed = State(true, 1, 0, Noon, false);

        Assert.Equal(PeerHistoryOutcome.Unchanged, PeerHistoryNegotiator.Decide(current, State(false, 0, 0, null, false), proposed));
    }

    /// <summary>
    /// The receiver's live state matches what the sender read, so the write goes ahead.
    /// </summary>
    [Fact]
    public void Decide_CurrentMatchesExpected_IsApplied()
    {
        var current = State(false, 0, 0, null, false);
        var expected = State(false, 0, 0, null, false);
        var proposed = State(true, 1, 0, Noon, false);

        Assert.Equal(PeerHistoryOutcome.Applied, PeerHistoryNegotiator.Decide(current, expected, proposed));
    }

    /// <summary>
    /// The receiver moved since the sender read it, so the sender must merge again.
    /// True: a play that happened on the receiver in the meantime is never overwritten blind.
    /// False: the last writer silently wins and a watched episode flips back to unwatched.
    /// </summary>
    [Fact]
    public void Decide_CurrentDiffersFromExpected_IsStale()
    {
        var current = State(true, 2, 500, Noon.AddHours(1), false);
        var expected = State(false, 0, 0, null, false);
        var proposed = State(true, 1, 0, Noon, false);

        Assert.Equal(PeerHistoryOutcome.Stale, PeerHistoryNegotiator.Decide(current, expected, proposed));
    }

    /// <summary>
    /// A sender with no reading of the receiver cannot know what it would overwrite, so it is told
    /// the live state and asked again.
    /// True: a row whose source user data was missing at refresh never writes blind.
    /// False: a transient gap in the source's response turns into a silent overwrite of its history.
    /// </summary>
    [Fact]
    public void Decide_NoExpectation_IsStale()
    {
        var current = State(true, 2, 500, Noon, false);
        var proposed = State(false, 0, 0, null, true);

        Assert.Equal(PeerHistoryOutcome.Stale, PeerHistoryNegotiator.Decide(current, null, proposed));
        Assert.Equal(PeerHistoryOutcome.Stale, PeerHistoryNegotiator.Decide(current, new PeerHistoryState(), proposed));
    }

    /// <summary>
    /// The sender folds a stale answer into the row and asks again, with the source fields replaced by
    /// the peer's live state and the merge rerun against them.
    /// </summary>
    [Fact]
    public void ResolveOutcome_StaleWithCurrent_RemergesAndRetries()
    {
        var record = new HistorySyncItem { SourceUserId = "u", SourceItemId = "i" };
        record.SourceIsPlayed = false;
        record.LocalIsPlayed = true;
        record.LocalLastPlayedDate = Noon;
        record.LocalPlayCount = 1;
        var live = State(true, 3, 0, Noon.AddDays(1), true);

        var step = PeerHistoryNegotiator.ResolveOutcome(record, new PeerHistoryResult { Outcome = PeerHistoryOutcome.Stale, Current = live }, allowRetry: true);

        Assert.Equal(NegotiationAction.Retry, step.Action);
        Assert.Equal(3, record.SourcePlayCount);
        Assert.True(record.SourceIsFavorite);
        Assert.Equal(3, record.MergedPlayCount);
        Assert.Equal(Noon.AddDays(1), record.MergedLastPlayedDate);
    }

    /// <summary>
    /// A second stale answer, or a stale answer without the live state, fails the row.
    /// </summary>
    [Fact]
    public void ResolveOutcome_StaleWithoutRetry_Fails()
    {
        var record = new HistorySyncItem { SourceUserId = "u", SourceItemId = "i" };

        var noRetry = PeerHistoryNegotiator.ResolveOutcome(record, new PeerHistoryResult { Outcome = PeerHistoryOutcome.Stale, Current = State(true, 1, 0, Noon, false) }, allowRetry: false);
        var noState = PeerHistoryNegotiator.ResolveOutcome(record, new PeerHistoryResult { Outcome = PeerHistoryOutcome.Stale }, allowRetry: true);

        Assert.Equal(NegotiationAction.Fail, noRetry.Action);
        Assert.Equal(NegotiationAction.Fail, noState.Action);
        Assert.Contains("kept changing", noRetry.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Applied and Unchanged both proceed to the local write. NotFound and Failed carry the peer's reason.
    /// </summary>
    [Fact]
    public void ResolveOutcome_TerminalAnswers()
    {
        var record = new HistorySyncItem { SourceUserId = "u", SourceItemId = "i" };

        Assert.Equal(NegotiationAction.Proceed, PeerHistoryNegotiator.ResolveOutcome(record, new PeerHistoryResult { Outcome = PeerHistoryOutcome.Applied }, true).Action);
        Assert.Equal(NegotiationAction.Proceed, PeerHistoryNegotiator.ResolveOutcome(record, new PeerHistoryResult { Outcome = PeerHistoryOutcome.Unchanged }, true).Action);

        var missing = PeerHistoryNegotiator.ResolveOutcome(record, new PeerHistoryResult { Outcome = PeerHistoryOutcome.NotFound, Reason = "gone" }, true);
        var failed = PeerHistoryNegotiator.ResolveOutcome(record, new PeerHistoryResult { Outcome = PeerHistoryOutcome.Failed, Reason = "disk" }, true);
        Assert.Equal(NegotiationAction.Fail, missing.Action);
        Assert.Contains("gone", missing.Reason, StringComparison.Ordinal);
        Assert.Contains("disk", failed.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Dates compare at second resolution because Jellyfin drops sub second precision.
    /// </summary>
    [Fact]
    public void StatesMatch_IgnoresSubSecondDateDifference()
    {
        var a = State(true, 1, 0, Noon, false);
        var b = State(true, 1, 0, Noon.AddMilliseconds(400), false);

        Assert.True(PeerHistoryNegotiator.StatesMatch(a, b));
    }

    /// <summary>
    /// A null field means no opinion and never counts as a difference.
    /// </summary>
    [Fact]
    public void StatesMatch_NullFieldMatchesAnything()
    {
        var a = new PeerHistoryState { IsFavorite = true };
        var b = State(true, 7, 123, Noon, true);

        Assert.True(PeerHistoryNegotiator.StatesMatch(a, b));
    }

    /// <summary>
    /// A state with a played flag and no date is an item marked unplayed, so it differs from one that
    /// still carries a date. Otherwise a mark as unplayed could never be pushed to a peer.
    /// </summary>
    [Fact]
    public void StatesMatch_ClearedDateAgainstKeptDate_IsMismatch()
    {
        var a = State(false, 0, 0, null, false);
        var b = State(false, 0, 0, Noon, false);

        Assert.False(PeerHistoryNegotiator.StatesMatch(a, b));
    }

    /// <summary>
    /// Two concrete values that differ are a mismatch.
    /// </summary>
    [Fact]
    public void StatesMatch_DifferentFavorite_IsMismatch()
    {
        var a = State(true, 1, 0, Noon, true);
        var b = State(true, 1, 0, Noon, false);

        Assert.False(PeerHistoryNegotiator.StatesMatch(a, b));
    }
}
