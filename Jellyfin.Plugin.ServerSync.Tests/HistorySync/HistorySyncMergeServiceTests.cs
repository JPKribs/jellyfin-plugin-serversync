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
}
