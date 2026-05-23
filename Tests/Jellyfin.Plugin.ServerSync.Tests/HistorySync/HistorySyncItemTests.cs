using System;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.HistorySync;

public class HistorySyncItemTests
{
    private static HistorySyncItem MakeQueueableItem() => new()
    {
        SourceUserId = "src-user",
        LocalUserId = "loc-user",
        SourceLibraryId = "src-lib",
        LocalLibraryId = "loc-lib",
        SourceItemId = "item-1",
        LocalItemId = "local-item-1"
    };

    /// <summary>
    /// Identical source fields produce identical bundle hashes.
    /// True: hash short-circuit will activate across refreshes when source hasn't moved.
    /// False: every refresh would re-evaluate every row regardless of source movement.
    /// </summary>
    [Fact]
    public void UpdateSourceStateBundle_ProducesSameHashForSameSourceFields()
    {
        var a = MakeQueueableItem();
        a.SourceIsPlayed = true;
        a.SourcePlayCount = 3;
        a.SourcePlaybackPositionTicks = 1000L;
        a.SourceLastPlayedDate = new DateTime(2025, 5, 23, 12, 0, 0, DateTimeKind.Utc);
        a.SourceIsFavorite = true;
        a.UpdateSourceStateBundle();

        var b = MakeQueueableItem();
        b.SourceIsPlayed = true;
        b.SourcePlayCount = 3;
        b.SourcePlaybackPositionTicks = 1000L;
        b.SourceLastPlayedDate = new DateTime(2025, 5, 23, 12, 0, 0, DateTimeKind.Utc);
        b.SourceIsFavorite = true;
        b.UpdateSourceStateBundle();

        Assert.Equal(a.SourceState.SourceHash, b.SourceState.SourceHash);
    }

    /// <summary>
    /// Changing any source field changes the bundle hash.
    /// True: source data moves are detected and the fast path correctly disengages.
    /// False: source moves would slip past the short-circuit and rows never get re-evaluated.
    /// </summary>
    [Fact]
    public void UpdateSourceStateBundle_DifferentFields_ProducesDifferentHash()
    {
        var a = MakeQueueableItem();
        a.SourcePlayCount = 3;
        a.UpdateSourceStateBundle();

        var b = MakeQueueableItem();
        b.SourcePlayCount = 4;
        b.UpdateSourceStateBundle();

        Assert.NotEqual(a.SourceState.SourceHash, b.SourceState.SourceHash);
    }

    /// <summary>
    /// Hash short-circuits HasChanges when SourceHash == SyncedHash even with a merge diff.
    /// True: source hasn't moved so we skip work, even if Local now differs from Merged.
    /// False: short-circuit isn't firing and rows are re-evaluated unnecessarily.
    /// </summary>
    [Fact]
    public void HasChanges_ShortCircuits_WhenSourceHashEqualsSyncedHash()
    {
        var item = MakeQueueableItem();
        item.SourceIsPlayed = true;
        item.UpdateSourceStateBundle();
        item.MarkSynced();

        item.MergedIsPlayed = true;
        item.LocalIsPlayed = false;

        Assert.False(item.HasChanges);
    }

    /// <summary>
    /// Source moved → fall through to the merge service.
    /// True: a genuine local-vs-merge divergence after source moves is detected.
    /// False: source moves wouldn't trigger re-sync even when local diverges.
    /// </summary>
    [Fact]
    public void HasChanges_FallsThroughToMerge_WhenSourceHashDiffers()
    {
        var item = MakeQueueableItem();
        item.SourceIsPlayed = true;
        item.UpdateSourceStateBundle();
        item.MarkSynced();

        item.SourceIsPlayed = false;
        item.UpdateSourceStateBundle();

        item.MergedIsPlayed = false;
        item.LocalIsPlayed = true;

        Assert.True(item.HasChanges);
    }

    /// <summary>
    /// Fresh row (never synced) uses the merge fallback because SyncedHash is null.
    /// True: first-run divergence between Merged and Local is caught.
    /// False: fresh rows would never queue even when they should.
    /// </summary>
    [Fact]
    public void HasChanges_FreshRow_NoSyncedHash_UsesMergeFallback()
    {
        var item = MakeQueueableItem();
        item.SourceIsPlayed = true;
        item.UpdateSourceStateBundle();

        item.MergedIsPlayed = true;
        item.LocalIsPlayed = false;

        Assert.True(item.HasChanges);
    }

    /// <summary>
    /// Fresh row with no diff returns no changes.
    /// True: idempotent first refresh marks the row Synced without doing extra work.
    /// False: fresh rows would always queue, regardless of divergence.
    /// </summary>
    [Fact]
    public void HasChanges_FreshRow_NoChanges_IsFalse()
    {
        var item = MakeQueueableItem();
        item.SourceIsPlayed = true;
        item.UpdateSourceStateBundle();

        item.MergedIsPlayed = true;
        item.LocalIsPlayed = true;

        Assert.False(item.HasChanges);
    }

    /// <summary>
    /// MarkSynced copies the SourceState hash to the synced hash.
    /// True: a subsequent refresh sees SourceHash == SyncedHash and short-circuits.
    /// False: SyncedHash never gets updated and the fast path never engages.
    /// </summary>
    [Fact]
    public void MarkSynced_CopiesSourceStateHashes()
    {
        var item = MakeQueueableItem();
        item.SourceIsPlayed = true;
        item.UpdateSourceStateBundle();

        Assert.NotNull(item.SourceState.SourceHash);
        Assert.Null(item.SourceState.SyncedHash);

        item.MarkSynced();

        Assert.Equal(item.SourceState.SourceHash, item.SourceState.SyncedHash);
    }

    /// <summary>
    /// MarkSynced after source moves updates SyncedHash to the latest source value.
    /// True: the next refresh short-circuits on the new state, not the old one.
    /// False: SyncedHash stuck on the first version masks subsequent legitimate source moves.
    /// </summary>
    [Fact]
    public void MarkSynced_AfterSourceMoves_SyncedHashCatchesUp()
    {
        var item = MakeQueueableItem();
        item.SourceIsPlayed = true;
        item.UpdateSourceStateBundle();
        item.MarkSynced();
        var first = item.SourceState.SyncedHash;

        item.SourceIsPlayed = false;
        item.UpdateSourceStateBundle();
        item.MarkSynced();

        Assert.NotEqual(first, item.SourceState.SyncedHash);
        Assert.Equal(item.SourceState.SourceHash, item.SourceState.SyncedHash);
    }
}
