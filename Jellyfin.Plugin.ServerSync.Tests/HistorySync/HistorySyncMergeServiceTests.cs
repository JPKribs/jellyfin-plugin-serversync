using System;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Services;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.HistorySync;

public class HistorySyncMergeServiceTests
{
    private static HistorySyncItem MakeItem() => new()
    {
        SourceUserId = "src-user",
        LocalUserId = "loc-user",
        SourceLibraryId = "src-lib",
        LocalLibraryId = "loc-lib",
        SourceItemId = "item-1"
    };

    /// <summary>
    /// IsFavorite always takes the source value.
    /// True: a favourite added on source flows through, regardless of local state.
    /// False: local users could override source favourites without a re-sync ever fixing it.
    /// </summary>
    [Fact]
    public void MergeHistoryData_IsFavorite_AlwaysFromSource()
    {
        var item = MakeItem();
        item.SourceIsFavorite = true;
        item.LocalIsFavorite = false;

        HistorySyncMergeService.MergeHistoryData(item);

        Assert.True(item.MergedIsFavorite);
    }

    /// <summary>
    /// Null SourceIsFavorite preserves the local value rather than wiping it.
    /// True: a transient response where source UserData is missing doesn't
    /// silently destroy local favorites.
    /// False: treating "no data received" as "not favorited" would zero out
    /// local favorites on any API hiccup.
    /// </summary>
    [Fact]
    public void MergeHistoryData_IsFavorite_PreservesLocalWhenSourceNull()
    {
        var item = MakeItem();
        item.SourceIsFavorite = null;
        item.LocalIsFavorite = true;

        HistorySyncMergeService.MergeHistoryData(item);

        Assert.True(item.MergedIsFavorite);
    }

    /// <summary>
    /// PlayCount takes MAX(source, local).
    /// True: higher count from either side wins, preserving "most-watched" semantics.
    /// False: lower count would overwrite the higher, losing watch history information.
    /// </summary>
    [Fact]
    public void MergeHistoryData_PlayCount_IsMaxOfSourceAndLocal()
    {
        var item = MakeItem();
        item.SourcePlayCount = 3;
        item.LocalPlayCount = 7;

        HistorySyncMergeService.MergeHistoryData(item);

        Assert.Equal(7, item.MergedPlayCount);
    }

    /// <summary>
    /// Null PlayCount is treated as zero for MAX comparison.
    /// True: an unset value never blocks the populated side from winning.
    /// False: null would propagate and silently zero out the play count.
    /// </summary>
    [Fact]
    public void MergeHistoryData_PlayCount_HandlesNullsAsZero()
    {
        var item = MakeItem();
        item.SourcePlayCount = null;
        item.LocalPlayCount = 4;

        HistorySyncMergeService.MergeHistoryData(item);

        Assert.Equal(4, item.MergedPlayCount);
    }

    /// <summary>
    /// Source wins Played/Position/LastPlayed when source has the more-recent LastPlayedDate.
    /// True: the most-recently-watched side's negotiated state propagates.
    /// False: stale state from the other side would overwrite fresh local state.
    /// </summary>
    [Fact]
    public void MergeHistoryData_NegotiatedFields_SourceWinsWhenMoreRecent()
    {
        var item = MakeItem();
        item.SourceLastPlayedDate = new DateTime(2025, 5, 23, 12, 0, 0, DateTimeKind.Utc);
        item.LocalLastPlayedDate = new DateTime(2025, 5, 22, 12, 0, 0, DateTimeKind.Utc);
        item.SourceIsPlayed = true;
        item.LocalIsPlayed = false;
        item.SourcePlaybackPositionTicks = 100L;
        item.LocalPlaybackPositionTicks = 50L;

        HistorySyncMergeService.MergeHistoryData(item);

        Assert.True(item.MergedIsPlayed);
        Assert.Equal(item.SourceLastPlayedDate, item.MergedLastPlayedDate);
        Assert.Equal(100L, item.MergedPlaybackPositionTicks);
    }

    /// <summary>
    /// Local wins Played/Position/LastPlayed when local has the more-recent LastPlayedDate.
    /// True: a fresh local watch isn't overwritten by older source state.
    /// False: replaying on local would be erased by older source data on next sync.
    /// </summary>
    [Fact]
    public void MergeHistoryData_NegotiatedFields_LocalWinsWhenMoreRecent()
    {
        var item = MakeItem();
        item.SourceLastPlayedDate = new DateTime(2025, 5, 22, 12, 0, 0, DateTimeKind.Utc);
        item.LocalLastPlayedDate = new DateTime(2025, 5, 23, 12, 0, 0, DateTimeKind.Utc);
        item.SourceIsPlayed = false;
        item.LocalIsPlayed = true;
        item.SourcePlaybackPositionTicks = 50L;
        item.LocalPlaybackPositionTicks = 100L;

        HistorySyncMergeService.MergeHistoryData(item);

        Assert.True(item.MergedIsPlayed);
        Assert.Equal(item.LocalLastPlayedDate, item.MergedLastPlayedDate);
        Assert.Equal(100L, item.MergedPlaybackPositionTicks);
    }

    /// <summary>
    /// Only source has a LastPlayed — its values are used.
    /// True: a brand-new local item gets seeded with the existing source watch state.
    /// False: source state would be ignored and local stays empty.
    /// </summary>
    [Fact]
    public void MergeHistoryData_OnlySourceHasDate_TakesSourceValues()
    {
        var item = MakeItem();
        item.SourceLastPlayedDate = new DateTime(2025, 5, 23, 0, 0, 0, DateTimeKind.Utc);
        item.LocalLastPlayedDate = null;
        item.SourceIsPlayed = true;
        item.LocalIsPlayed = false;

        HistorySyncMergeService.MergeHistoryData(item);

        Assert.True(item.MergedIsPlayed);
        Assert.Equal(item.SourceLastPlayedDate, item.MergedLastPlayedDate);
    }

    /// <summary>
    /// Only local has a LastPlayed — its values are used.
    /// True: existing local watch state survives a re-add on the source side.
    /// False: local watch state would be wiped when the source has no opinion.
    /// </summary>
    [Fact]
    public void MergeHistoryData_OnlyLocalHasDate_TakesLocalValues()
    {
        var item = MakeItem();
        item.SourceLastPlayedDate = null;
        item.LocalLastPlayedDate = new DateTime(2025, 5, 23, 0, 0, 0, DateTimeKind.Utc);
        item.SourceIsPlayed = false;
        item.LocalIsPlayed = true;

        HistorySyncMergeService.MergeHistoryData(item);

        Assert.True(item.MergedIsPlayed);
        Assert.Equal(item.LocalLastPlayedDate, item.MergedLastPlayedDate);
    }

    /// <summary>
    /// Neither side has a date — fall back to source values.
    /// True: the merge service is deterministic even when both sides are date-less.
    /// False: a no-date row would have undefined merge results.
    /// </summary>
    [Fact]
    public void MergeHistoryData_NeitherDate_FallsBackToSourceValues()
    {
        var item = MakeItem();
        item.SourceLastPlayedDate = null;
        item.LocalLastPlayedDate = null;
        item.SourceIsPlayed = true;
        item.LocalIsPlayed = false;
        item.SourcePlaybackPositionTicks = 42L;
        item.LocalPlaybackPositionTicks = null;

        HistorySyncMergeService.MergeHistoryData(item);

        Assert.True(item.MergedIsPlayed);
        Assert.Null(item.MergedLastPlayedDate);
        Assert.Equal(42L, item.MergedPlaybackPositionTicks);
    }

    /// <summary>
    /// Equal LastPlayedDate ties favour source.
    /// True: source wins the tie-break, matching the >= check in MergeNegotiatedHistory.
    /// False: tie-breaking would be non-deterministic and runs would yield different results.
    /// </summary>
    [Fact]
    public void MergeHistoryData_EqualDates_FallsToSource()
    {
        var dt = new DateTime(2025, 5, 23, 12, 0, 0, DateTimeKind.Utc);
        var item = MakeItem();
        item.SourceLastPlayedDate = dt;
        item.LocalLastPlayedDate = dt;
        item.SourceIsPlayed = true;
        item.LocalIsPlayed = false;

        HistorySyncMergeService.MergeHistoryData(item);

        Assert.True(item.MergedIsPlayed);
    }

    /// <summary>
    /// A merged-vs-local IsPlayed mismatch is detected as a change.
    /// True: queue this row for sync because Played state differs.
    /// False: divergent Played would never trigger a sync.
    /// </summary>
    [Fact]
    public void HasChangesToSync_PlayedDifference_IsTrue()
    {
        var item = MakeItem();
        item.LocalIsPlayed = false;
        item.MergedIsPlayed = true;

        Assert.True(HistorySyncMergeService.HasChangesToSync(item));
    }

    /// <summary>
    /// A merged-vs-local PlayCount mismatch is detected as a change.
    /// True: queue this row for sync because PlayCount differs.
    /// False: divergent PlayCount would never trigger a sync.
    /// </summary>
    [Fact]
    public void HasChangesToSync_PlayCountDifference_IsTrue()
    {
        var item = MakeItem();
        item.LocalPlayCount = 2;
        item.MergedPlayCount = 5;

        Assert.True(HistorySyncMergeService.HasChangesToSync(item));
    }

    /// <summary>
    /// A merged-vs-local Favorite mismatch is detected as a change.
    /// True: queue this row for sync because Favorite differs.
    /// False: divergent Favorite would never trigger a sync.
    /// </summary>
    [Fact]
    public void HasChangesToSync_FavoriteDifference_IsTrue()
    {
        var item = MakeItem();
        item.LocalIsFavorite = false;
        item.MergedIsFavorite = true;

        Assert.True(HistorySyncMergeService.HasChangesToSync(item));
    }

    /// <summary>
    /// A merged-vs-local PlaybackPosition mismatch is detected as a change.
    /// True: queue this row for sync because position differs.
    /// False: position resumes wouldn't propagate.
    /// </summary>
    [Fact]
    public void HasChangesToSync_PositionDifference_IsTrue()
    {
        var item = MakeItem();
        item.LocalPlaybackPositionTicks = 100L;
        item.MergedPlaybackPositionTicks = 200L;

        Assert.True(HistorySyncMergeService.HasChangesToSync(item));
    }

    /// <summary>
    /// A merged-vs-local LastPlayedDate mismatch is detected as a change.
    /// True: queue this row for sync because LastPlayed differs.
    /// False: stale LastPlayed wouldn't sync, even if other fields do.
    /// </summary>
    [Fact]
    public void HasChangesToSync_LastPlayedDateDifference_IsTrue()
    {
        var item = MakeItem();
        item.LocalLastPlayedDate = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        item.MergedLastPlayedDate = new DateTime(2025, 5, 23, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(HistorySyncMergeService.HasChangesToSync(item));
    }

    /// <summary>
    /// All merged fields equal their local counterparts — no change to sync.
    /// True: synced rows stay Synced rather than being re-queued.
    /// False: idempotent refreshes would queue every row every run.
    /// </summary>
    [Fact]
    public void HasChangesToSync_AllMatch_IsFalse()
    {
        var item = MakeItem();
        item.LocalIsPlayed = true;
        item.MergedIsPlayed = true;
        item.LocalPlayCount = 3;
        item.MergedPlayCount = 3;
        item.LocalIsFavorite = true;
        item.MergedIsFavorite = true;
        item.LocalPlaybackPositionTicks = 50L;
        item.MergedPlaybackPositionTicks = 50L;
        var dt = new DateTime(2025, 5, 23, 12, 0, 0, DateTimeKind.Utc);
        item.LocalLastPlayedDate = dt;
        item.MergedLastPlayedDate = dt;

        Assert.False(HistorySyncMergeService.HasChangesToSync(item));
    }

    /// <summary>
    /// LocalItemId being absent does not suppress a real merge diff.
    /// True: BuildRecord never persists a row without a LocalItemId, so this is
    /// belt-and-suspenders — but if a future code path ever does, a real diff
    /// is still surfaced.
    /// False: silent suppression would mask actual divergences for orphan rows.
    /// </summary>
    [Fact]
    public void HasChangesToSync_NoLocalItemId_WithMergeDiff_StillReturnsTrue()
    {
        var item = MakeItem();
        item.LocalItemId = null;
        item.MergedIsPlayed = true;
        item.LocalIsPlayed = false;

        Assert.True(HistorySyncMergeService.HasChangesToSync(item));
    }

    /// <summary>
    /// LocalItemId being absent + no merge diff returns false.
    /// True: a row with no divergence and no local correlate is correctly
    /// reported as unchanged.
    /// False: idempotent rows would falsely claim they need syncing.
    /// </summary>
    [Fact]
    public void HasChangesToSync_NoLocalItemId_NoMergeDiff_ReturnsFalse()
    {
        var item = MakeItem();
        item.LocalItemId = null;
        item.MergedIsPlayed = null;
        item.LocalIsPlayed = null;

        Assert.False(HistorySyncMergeService.HasChangesToSync(item));
    }

    /// <summary>
    /// Default item with no changes returns the "No changes" sentinel.
    /// True: ChangesSummary in the modal correctly reads as "No changes" on Synced rows.
    /// False: noisy summaries on idempotent rows confuse the operator.
    /// </summary>
    [Fact]
    public void GetChangeSummary_NoChanges_ReturnsNoChanges()
    {
        var item = MakeItem();

        Assert.Equal("No changes", HistorySyncMergeService.GetChangeSummary(item));
    }

    /// <summary>
    /// A Played change is listed by name with the True/False transition shown.
    /// True: log output names the specific transition for diagnosis.
    /// False: generic "changes detected" would force operators to dive into the row.
    /// </summary>
    [Fact]
    public void GetChangeSummary_PlayedChange_IsListed()
    {
        var item = MakeItem();
        item.LocalIsPlayed = false;
        item.MergedIsPlayed = true;

        var summary = HistorySyncMergeService.GetChangeSummary(item);

        Assert.Contains("Played", summary);
        Assert.Contains("True", summary);
    }

    /// <summary>
    /// Multiple field changes are all listed in the summary.
    /// True: all diverging fields are surfaced at once instead of just the first.
    /// False: only the first change is named and operators miss the rest.
    /// </summary>
    [Fact]
    public void GetChangeSummary_MultipleChanges_AllListed()
    {
        var item = MakeItem();
        item.LocalIsPlayed = false;
        item.MergedIsPlayed = true;
        item.LocalPlayCount = 0;
        item.MergedPlayCount = 3;
        item.LocalIsFavorite = false;
        item.MergedIsFavorite = true;

        var summary = HistorySyncMergeService.GetChangeSummary(item);

        Assert.Contains("Played", summary);
        Assert.Contains("PlayCount", summary);
        Assert.Contains("Favorite", summary);
    }

    // ===================================================================
    // Sub-second drift. Jellyfin does not round-trip sub-second precision
    // through its user-data store, so the value read back after an apply is
    // not bit-identical to the value written. With the SourceHash
    // short-circuit removed, an exact comparison here would report a change
    // on the very row we just synced and requeue it on every run forever.
    // ===================================================================

    /// <summary>
    /// A LastPlayedDate differing only below the second is not a change.
    /// True: a synced row settles and stays out of the queue.
    /// False: the row requeues on every refresh forever — the change detector
    /// insists it differs while the verifier insists the write landed.
    /// </summary>
    [Fact]
    public void HasChangesToSync_SubSecondLastPlayedDrift_IsNotAChange()
    {
        var item = MakeItem();
        item.MergedIsPlayed = true;
        item.LocalIsPlayed = true;
        item.MergedPlayCount = 3;
        item.LocalPlayCount = 3;
        item.MergedPlaybackPositionTicks = 100;
        item.LocalPlaybackPositionTicks = 100;
        item.MergedIsFavorite = false;
        item.LocalIsFavorite = false;

        item.MergedLastPlayedDate = new DateTime(2026, 7, 31, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234567);
        item.LocalLastPlayedDate = new DateTime(2026, 7, 31, 10, 0, 0, DateTimeKind.Utc);

        Assert.False(HistorySyncMergeService.HasChangesToSync(item));
    }

    /// <summary>
    /// A whole-second difference is still a real change.
    /// True: the tolerance is narrow enough to keep detecting genuine playback.
    /// False: rounding to seconds has swallowed real differences.
    /// </summary>
    [Fact]
    public void HasChangesToSync_WholeSecondLastPlayedDifference_IsAChange()
    {
        var item = MakeItem();
        item.MergedIsPlayed = true;
        item.LocalIsPlayed = true;
        item.MergedPlayCount = 3;
        item.LocalPlayCount = 3;
        item.MergedPlaybackPositionTicks = 100;
        item.LocalPlaybackPositionTicks = 100;
        item.MergedIsFavorite = false;
        item.LocalIsFavorite = false;

        item.MergedLastPlayedDate = new DateTime(2026, 7, 31, 10, 0, 5, DateTimeKind.Utc);
        item.LocalLastPlayedDate = new DateTime(2026, 7, 31, 10, 0, 0, DateTimeKind.Utc);

        Assert.True(HistorySyncMergeService.HasChangesToSync(item));
    }

    /// <summary>
    /// Merged has a date and local has none — a real change.
    /// True: first-time history for an item is pushed to local.
    /// False: never-played local items stay unsynced.
    /// </summary>
    [Fact]
    public void HasChangesToSync_LocalHasNoDate_IsAChange()
    {
        var item = MakeItem();
        item.MergedIsPlayed = true;
        item.LocalIsPlayed = true;
        item.MergedPlayCount = 1;
        item.LocalPlayCount = 1;
        item.MergedIsFavorite = false;
        item.LocalIsFavorite = false;
        item.MergedLastPlayedDate = new DateTime(2026, 7, 31, 10, 0, 0, DateTimeKind.Utc);
        item.LocalLastPlayedDate = null;

        Assert.True(HistorySyncMergeService.HasChangesToSync(item));
    }

    /// <summary>
    /// The shared instant comparison used by both the detector and the verifier.
    /// True: they can never disagree about whether a write landed.
    /// False: one says "changed" while the other says "applied", and the row
    /// oscillates between Queued and Synced.
    /// </summary>
    [Fact]
    public void SameInstantToSecond_MatchesVerifierSemantics()
    {
        var baseTime = new DateTime(2026, 7, 31, 10, 0, 0, DateTimeKind.Utc);

        Assert.True(HistorySyncMergeService.SameInstantToSecond(null, null));
        Assert.False(HistorySyncMergeService.SameInstantToSecond(baseTime, null));
        Assert.False(HistorySyncMergeService.SameInstantToSecond(null, baseTime));
        Assert.True(HistorySyncMergeService.SameInstantToSecond(baseTime, baseTime.AddTicks(9999999)));
        Assert.False(HistorySyncMergeService.SameInstantToSecond(baseTime, baseTime.AddSeconds(1)));
    }

    private static HistorySyncItem MakeAgreedItem()
    {
        var item = MakeItem();
        item.MergedIsPlayed = true;
        item.MergedPlayCount = 1;
        item.MergedPlaybackPositionTicks = 0;
        item.MergedLastPlayedDate = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        item.MergedIsFavorite = true;
        item.RecordNegotiatedBase(new DateTime(2026, 1, 1, 12, 5, 0, DateTimeKind.Utc));

        item.SourceIsPlayed = item.LocalIsPlayed = true;
        item.SourcePlayCount = item.LocalPlayCount = 1;
        item.SourcePlaybackPositionTicks = item.LocalPlaybackPositionTicks = 0;
        item.SourceLastPlayedDate = item.LocalLastPlayedDate = item.MergedLastPlayedDate;
        item.SourceIsFavorite = item.LocalIsFavorite = true;
        return item;
    }

    /// <summary>
    /// With an agreed base, a favorite removed only on the local server wins over an unchanged source.
    /// True: an unfavorite on either server reaches the other.
    /// False: the source keeps restoring a favorite the user removed.
    /// </summary>
    [Fact]
    public void MergeAgainstBase_LocalUnfavorite_WinsOverUnchangedSource()
    {
        var item = MakeAgreedItem();
        item.LocalIsFavorite = false;

        HistorySyncMergeService.MergeHistoryData(item);

        Assert.False(item.MergedIsFavorite);
    }

    /// <summary>
    /// With an agreed base, a favorite changed only on the source still flows to local.
    /// </summary>
    [Fact]
    public void MergeAgainstBase_SourceUnfavorite_WinsOverUnchangedLocal()
    {
        var item = MakeAgreedItem();
        item.SourceIsFavorite = false;

        HistorySyncMergeService.MergeHistoryData(item);

        Assert.False(item.MergedIsFavorite);
    }

    /// <summary>
    /// When both servers changed the favorite since the base, favorite wins.
    /// </summary>
    [Fact]
    public void MergeAgainstBase_BothChangedFavorite_FavoriteWins()
    {
        var item = MakeAgreedItem();
        item.NegotiatedIsFavorite = false;
        item.SourceIsFavorite = true;
        item.LocalIsFavorite = false;
        item.LocalPlayCount = 1;

        HistorySyncMergeService.MergeHistoryData(item);

        Assert.True(item.MergedIsFavorite);
    }

    /// <summary>
    /// Marking an item unplayed on the local server alone wins even though the source still carries
    /// the more recent play date. Without the base, the date rule would resurrect the play forever.
    /// </summary>
    [Fact]
    public void MergeAgainstBase_LocalMarkedUnplayed_WinsOverUnchangedSource()
    {
        var item = MakeAgreedItem();
        item.LocalIsPlayed = false;
        item.LocalLastPlayedDate = null;

        HistorySyncMergeService.MergeHistoryData(item);

        Assert.False(item.MergedIsPlayed);
        Assert.Null(item.MergedLastPlayedDate);
    }

    /// <summary>
    /// When both servers played since the base, the more recent play wins as before.
    /// </summary>
    [Fact]
    public void MergeAgainstBase_BothPlayed_MostRecentWins()
    {
        var item = MakeAgreedItem();
        item.SourceLastPlayedDate = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        item.SourcePlaybackPositionTicks = 100;
        item.LocalLastPlayedDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        item.LocalPlaybackPositionTicks = 200;

        HistorySyncMergeService.MergeHistoryData(item);

        Assert.Equal(200, item.MergedPlaybackPositionTicks);
        Assert.Equal(item.LocalLastPlayedDate, item.MergedLastPlayedDate);
    }

    /// <summary>
    /// A play count lowered on one server alone propagates, which the plain max rule can never do.
    /// </summary>
    [Fact]
    public void MergeAgainstBase_PlayCountResetOnOneSide_Propagates()
    {
        var item = MakeAgreedItem();
        item.NegotiatedPlayCount = 5;
        item.SourcePlayCount = 5;
        item.LocalPlayCount = 0;

        HistorySyncMergeService.MergeHistoryData(item);

        Assert.Equal(0, item.MergedPlayCount);
    }

    /// <summary>
    /// Nothing moved on either side, so the merge equals the base and nothing is queued.
    /// </summary>
    [Fact]
    public void MergeAgainstBase_NothingMoved_NoChanges()
    {
        var item = MakeAgreedItem();

        HistorySyncMergeService.MergeHistoryData(item);

        Assert.False(HistorySyncMergeService.HasChangesToSync(item));
    }

    /// <summary>
    /// Without a base the old rules still apply, so one way installs behave exactly as before.
    /// </summary>
    [Fact]
    public void MergeHistoryData_WithoutBase_SourceFavoriteStillWins()
    {
        var item = MakeItem();
        item.SourceIsFavorite = true;
        item.LocalIsFavorite = false;

        HistorySyncMergeService.MergeHistoryData(item);

        Assert.False(item.HasNegotiatedBase);
        Assert.True(item.MergedIsFavorite);
    }

    /// <summary>
    /// In two way mode a row whose local side already matches the merge still queues when the source
    /// is behind, otherwise a change made only here never reaches the source.
    /// </summary>
    [Fact]
    public void HasChangesToSync_SourceBehind_QueuesOnlyWhenNegotiating()
    {
        var item = MakeAgreedItem();
        item.LocalIsFavorite = false;
        HistorySyncMergeService.MergeHistoryData(item);
        Assert.False(item.MergedIsFavorite);

        item.NegotiateWithSource = false;
        Assert.False(HistorySyncMergeService.HasChangesToSync(item));

        item.NegotiateWithSource = true;
        Assert.True(HistorySyncMergeService.HasChangesToSync(item));
    }

    /// <summary>
    /// A merge to unplayed with no date counts as a change while the local row still carries a date.
    /// </summary>
    [Fact]
    public void HasChangesToSync_UnplayedMergeWithLingeringLocalDate_IsChange()
    {
        var item = MakeAgreedItem();
        item.SourceIsPlayed = false;
        item.SourcePlayCount = 0;
        item.SourceLastPlayedDate = null;
        HistorySyncMergeService.MergeHistoryData(item);

        Assert.False(item.MergedIsPlayed);
        Assert.Null(item.MergedLastPlayedDate);
        Assert.True(HistorySyncMergeService.HasChangesToSync(item));
    }

    /// <summary>
    /// Before the servers have agreed once, two way mode keeps favorites from both sides, so enabling
    /// negotiation never wipes a favorite that exists only locally.
    /// </summary>
    [Fact]
    public void MergeHistoryData_NegotiatingWithoutBase_FavoritesAreUnion()
    {
        var item = MakeItem();
        item.NegotiateWithSource = true;
        item.SourceIsFavorite = false;
        item.LocalIsFavorite = true;

        HistorySyncMergeService.MergeHistoryData(item);

        Assert.True(item.MergedIsFavorite);
        Assert.True(HistorySyncMergeService.HasChangesToSync(item));
    }
}
