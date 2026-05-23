using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Common.Comparators;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Common;

public class SyncableValueTests
{
    private static SyncableValue<string> NewValue() => new()
    {
        Comparator = new JsonBlobComparator()
    };

    /// <summary>
    /// The SourceHash == SyncedHash fast path suppresses the deep compare.
    /// True: source hasn't moved since the last sync, so no work is needed even if Local diverges.
    /// False: the short-circuit isn't firing and every refresh would re-evaluate from scratch.
    /// </summary>
    [Fact]
    public void HasChanges_IsFalse_WhenSourceHashEqualsSyncedHash()
    {
        var v = NewValue();
        v.UpdateSource("{\"a\":1}");
        v.MarkSynced();
        v.Local = "{\"a\":2}";

        Assert.False(v.HasChanges);
    }

    /// <summary>
    /// Source moved and Source still differs from Local, so HasChanges fires.
    /// True: a real divergence between current Source and Local is detected.
    /// False: the fall-through comparator path isn't running — divergences silently ignored.
    /// </summary>
    [Fact]
    public void HasChanges_IsTrue_WhenSourceHashDiffersFromSyncedHash_AndSourceDiffersFromLocal()
    {
        var v = NewValue();
        v.UpdateSource("{\"a\":1}");
        v.MarkSynced();
        v.UpdateSource("{\"a\":2}");
        v.Local = "{\"a\":1}";

        Assert.True(v.HasChanges);
    }

    /// <summary>
    /// Fresh row (never synced) where Source equals Local returns no changes.
    /// True: comparator correctly identifies equal source/local without needing prior sync history.
    /// False: HasChanges defaulting to true on fresh rows would queue every just-built record.
    /// </summary>
    [Fact]
    public void HasChanges_IsFalse_WhenSourceMatchesLocal_AndHashesUnseeded()
    {
        var v = NewValue();
        v.Source = "{\"a\":1}";
        v.Local = "{\"a\":1}";

        Assert.False(v.HasChanges);
    }

    /// <summary>
    /// Fresh row with Source != Local fires changes via comparator (no hashes yet).
    /// True: first-time refresh queues genuinely divergent rows even without any sync history.
    /// False: divergent rows on first refresh would be silently marked Synced.
    /// </summary>
    [Fact]
    public void HasChanges_IsTrue_WhenSourceDiffersFromLocal_AndHashesUnseeded()
    {
        var v = NewValue();
        v.Source = "{\"a\":1}";
        v.Local = "{\"a\":2}";

        Assert.True(v.HasChanges);
    }

    /// <summary>
    /// UpdateSource assigns Source and recomputes the source hash in one step.
    /// True: after UpdateSource, both Source and SourceHash are populated from the input.
    /// False: callers would have to remember to call RecomputeSourceHash manually, easy to forget.
    /// </summary>
    [Fact]
    public void UpdateSource_AssignsSourceAndRecomputesHash()
    {
        var v = NewValue();
        Assert.Null(v.SourceHash);

        v.UpdateSource("{\"x\":42}");

        Assert.Equal("{\"x\":42}", v.Source);
        Assert.NotNull(v.SourceHash);
        Assert.NotEmpty(v.SourceHash);
    }

    /// <summary>
    /// UpdateSource(null) clears Source and produces a null hash.
    /// True: nulling Source produces a null hash so SourceHash != SyncedHash never accidentally fires.
    /// False: null source would carry a stale hash and falsely short-circuit on subsequent runs.
    /// </summary>
    [Fact]
    public void UpdateSource_WithNull_ResultsInNullHash()
    {
        var v = NewValue();
        v.UpdateSource("{\"x\":42}");
        Assert.NotNull(v.SourceHash);

        v.UpdateSource(null);

        Assert.Null(v.Source);
        Assert.Null(v.SourceHash);
    }

    /// <summary>
    /// Hashing the same JSON twice produces the same fingerprint.
    /// True: hashes are reproducible across refresh runs so SourceHash == SyncedHash actually fires.
    /// False: non-deterministic hashing breaks the fast path entirely — every row re-evaluated.
    /// </summary>
    [Fact]
    public void RecomputeSourceHash_StableForSameInput()
    {
        var v1 = NewValue();
        v1.UpdateSource("{\"x\":42}");

        var v2 = NewValue();
        v2.UpdateSource("{\"x\":42}");

        Assert.Equal(v1.SourceHash, v2.SourceHash);
    }

    /// <summary>
    /// Different JSON content produces different hashes.
    /// True: changed source data is detectable via hash, triggering re-sync.
    /// False: hash collisions on differing data hide real changes and mark divergent rows Synced.
    /// </summary>
    [Fact]
    public void RecomputeSourceHash_DiffersForDifferentInputs()
    {
        var v1 = NewValue();
        v1.UpdateSource("{\"x\":42}");

        var v2 = NewValue();
        v2.UpdateSource("{\"x\":43}");

        Assert.NotEqual(v1.SourceHash, v2.SourceHash);
    }

    /// <summary>
    /// MarkSynced copies Source/SourceHash into Synced/SyncedHash.
    /// True: post-apply, the next refresh sees SourceHash == SyncedHash and short-circuits.
    /// False: SyncedHash never gets populated and the fast path never activates.
    /// </summary>
    [Fact]
    public void MarkSynced_CopiesSourceToSyncedAndSourceHashToSyncedHash()
    {
        var v = NewValue();
        v.UpdateSource("{\"x\":42}");

        Assert.Null(v.Synced);
        Assert.Null(v.SyncedHash);

        v.MarkSynced();

        Assert.Equal(v.Source, v.Synced);
        Assert.Equal(v.SourceHash, v.SyncedHash);
    }

    /// <summary>
    /// A second MarkSynced after source moves updates SyncedHash to the new source.
    /// True: after a second sync cycle SyncedHash tracks the latest source state.
    /// False: SyncedHash stuck on the first sync would falsely short-circuit on subsequent changes.
    /// </summary>
    [Fact]
    public void MarkSynced_AfterSourceMoves_SyncedHashCatchesUp()
    {
        var v = NewValue();
        v.UpdateSource("{\"x\":1}");
        v.MarkSynced();
        var firstSynced = v.SyncedHash;

        v.UpdateSource("{\"x\":2}");
        v.MarkSynced();

        Assert.Equal(v.SourceHash, v.SyncedHash);
        Assert.NotEqual(firstSynced, v.SyncedHash);
    }
}
