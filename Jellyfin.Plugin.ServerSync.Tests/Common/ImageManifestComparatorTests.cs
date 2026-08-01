using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Common.Comparators;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Common;

public class ImageManifestComparatorTests
{
    private static readonly ImageManifestComparator Cmp = new();

    private static string Manifest(params (string Type, int Index, long Size, int Width, int Height, string? Tag)[] images)
    {
        var map = new Dictionary<string, List<ImageInfoDto>>();
        foreach (var (type, idx, size, w, h, tag) in images)
        {
            if (!map.TryGetValue(type, out var list))
            {
                list = new List<ImageInfoDto>();
                map[type] = list;
            }

            list.Add(new ImageInfoDto
            {
                ImageType = type,
                ImageIndex = idx,
                Size = size,
                Width = w,
                Height = h,
                Tag = tag
            });
        }

        return JsonSerializer.Serialize(map);
    }

    /// <summary>
    /// Null or empty source is treated as "nothing to sync" and matches anything.
    /// True: empty source skips apply rather than forcing a zero-payload write.
    /// False: empty-source rows would queue an apply that can't deliver and then fail verify.
    /// </summary>
    [Fact]
    public void Equals_EmptyOrNullSource_IsTreatedAsMatch()
    {
        Assert.True(Cmp.Equals(null, "{}"));
        Assert.True(Cmp.Equals(string.Empty, Manifest(("Primary", 0, 100, 1, 1, "t"))));
    }

    /// <summary>
    /// Non-empty source with empty local compares not-equal.
    /// True: a fresh local needing images is correctly queued.
    /// False: rows that need an initial image download would skip the apply and never get one.
    /// </summary>
    [Fact]
    public void Equals_EmptyLocal_NonEmptySource_IsDifferent()
    {
        var source = Manifest(("Primary", 0, 100, 1, 1, "t"));
        Assert.False(Cmp.Equals(source, null));
        Assert.False(Cmp.Equals(source, string.Empty));
    }

    /// <summary>
    /// Identical type/index/size manifests compare equal.
    /// True: no spurious diffs when both sides have the same image set.
    /// False: every refresh would mark images as changed, re-applying needlessly.
    /// </summary>
    [Fact]
    public void Equals_MatchingSizes_AreEqual()
    {
        var src = Manifest(("Primary", 0, 5000, 100, 100, "t"));
        var loc = Manifest(("Primary", 0, 5000, 100, 100, "t"));

        Assert.True(Cmp.Equals(src, loc));
    }

    /// <summary>
    /// Same image at different sizes compares not-equal.
    /// True: real image-content changes are detected and queued.
    /// False: re-encoded images would silently stay desynced.
    /// </summary>
    [Fact]
    public void Equals_DifferentSizes_AreDifferent()
    {
        var src = Manifest(("Primary", 0, 5000, 100, 100, "t"));
        var loc = Manifest(("Primary", 0, 6000, 100, 100, "t"));

        Assert.False(Cmp.Equals(src, loc));
    }

    // The tag-only-source case is covered below in the degraded-manifest
    // section; it is now indeterminate rather than a difference. See
    // Equals_TagOnlySourceVsSizedLocal_IsNotADifference for why.

    /// <summary>
    /// Sized source and Size=0 local (missing file) compare not-equal.
    /// True: hollow local images get queued for re-download.
    /// False: an unreadable local file would silently stay broken forever.
    /// </summary>
    [Fact]
    public void Equals_SizedSource_MissingLocalFile_IsDifferent()
    {
        var src = Manifest(("Primary", 0, 5000, 100, 100, "t"));
        var loc = Manifest(("Primary", 0, 0, 0, 0, "t"));

        Assert.False(Cmp.Equals(src, loc));
    }

    /// <summary>
    /// Both sides Size=0 (indeterminate) compare equal.
    /// True: rows stuck in the no-data limbo aren't permanently pinned to Errored.
    /// False: rows where neither side has size info would never reach Synced.
    /// </summary>
    [Fact]
    public void Equals_BothZeroSize_TreatedAsMatch()
    {
        var src = Manifest(("Primary", 0, 0, 0, 0, "t"));
        var loc = Manifest(("Primary", 0, 0, 0, 0, "t"));

        Assert.True(Cmp.Equals(src, loc));
    }

    /// <summary>
    /// Source has an image type that local doesn't, compare not-equal.
    /// True: missing-on-local types correctly trigger a fetch.
    /// False: missing types would never be added on local.
    /// </summary>
    [Fact]
    public void Equals_MissingTypeOnLocal_IsDifferent()
    {
        var src = Manifest(("Primary", 0, 100, 1, 1, "t"), ("Backdrop", 0, 200, 1, 1, "t2"));
        var loc = Manifest(("Primary", 0, 100, 1, 1, "t"));

        Assert.False(Cmp.Equals(src, loc));
    }

    /// <summary>
    /// Different image count for the same type compares not-equal.
    /// True: count mismatches trigger re-sync.
    /// False: rows with extra backdrops on one side would stay Synced.
    /// </summary>
    [Fact]
    public void Equals_DifferentCount_IsDifferent()
    {
        var src = Manifest(("Backdrop", 0, 100, 1, 1, "a"), ("Backdrop", 1, 100, 1, 1, "b"));
        var loc = Manifest(("Backdrop", 0, 100, 1, 1, "a"));

        Assert.False(Cmp.Equals(src, loc));
    }

    /// <summary>
    /// Local has extra types beyond source — comparator is one-directional and tolerates this.
    /// True: source-vs-local direction only, local can retain types source doesn't have.
    /// False: local extras would force a wipe, defeating the "sync from source" intent.
    /// </summary>
    [Fact]
    public void Equals_ExtraLocalTypes_TolerantOfLocalSuperset()
    {
        var src = Manifest(("Primary", 0, 100, 1, 1, "t"));
        var loc = Manifest(("Primary", 0, 100, 1, 1, "t"), ("Logo", 0, 50, 1, 1, "l"));

        Assert.True(Cmp.Equals(src, loc));
    }

    /// <summary>
    /// Same manifest hashed twice yields the same fingerprint.
    /// True: SourceHash is stable across refreshes so the fast path activates.
    /// False: non-deterministic hashing breaks the SourceHash == SyncedHash short-circuit.
    /// </summary>
    [Fact]
    public void ComputeHash_StableForSameInput()
    {
        var m = Manifest(("Primary", 0, 5000, 100, 100, "t"));

        Assert.Equal(Cmp.ComputeHash(m), Cmp.ComputeHash(m));
    }

    /// <summary>
    /// Different image Tag with same size produces different hash.
    /// True: re-encoded images (Tag changes when pixels change) invalidate the fast path.
    /// False: re-encoded images would never re-sync because hash didn't move.
    /// </summary>
    [Fact]
    public void ComputeHash_TagChange_ChangesHash()
    {
        var a = Manifest(("Primary", 0, 0, 0, 0, "tag-a"));
        var b = Manifest(("Primary", 0, 0, 0, 0, "tag-b"));

        Assert.NotEqual(Cmp.ComputeHash(a), Cmp.ComputeHash(b));
    }

    /// <summary>
    /// Null or empty input produces a null hash.
    /// True: empty manifests don't carry a hash that could accidentally short-circuit.
    /// False: a non-null hash for empty input would cause spurious Synced rows.
    /// </summary>
    [Fact]
    public void ComputeHash_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(Cmp.ComputeHash(null));
        Assert.Null(Cmp.ComputeHash(string.Empty));
    }

    /// <summary>
    /// Empty map ({}) hashes to null.
    /// True: serialised-but-empty manifests are treated the same as null/empty strings.
    /// False: empty maps would short-circuit on a non-null hash, hiding any later source data.
    /// </summary>
    [Fact]
    public void ComputeHash_EmptyMap_ReturnsNull()
    {
        Assert.Null(Cmp.ComputeHash("{}"));
    }

    /// <summary>
    /// DescribeDifference points at the specific type and sizes that diverged.
    /// True: verify-failure logs name the precise field so operators can diagnose without guessing.
    /// False: generic "manifests differ" message wastes time on troubleshooting.
    /// </summary>
    [Fact]
    public void DescribeDifference_NamesTheDivergingType()
    {
        var src = Manifest(("Primary", 0, 5000, 100, 100, "t"));
        var loc = Manifest(("Primary", 0, 6000, 100, 100, "t"));

        var desc = Cmp.DescribeDifference(src, loc);

        Assert.NotNull(desc);
        Assert.Contains("Primary", desc);
        Assert.Contains("5000", desc);
        Assert.Contains("6000", desc);
    }

    // ===================================================================
    // Degraded (tag-only) source manifests. A source Size of 0 means
    // enrichment could not measure the image — /Items/{id}/Images failed,
    // which a non-admin token reproduces with a 403.
    // ===================================================================

    /// <summary>
    /// A tag-only source entry against a sized local entry is indeterminate,
    /// not a difference.
    /// True: enrichment failure degrades to count-only comparison and the row settles.
    /// False: the row queues, sync re-downloads every image, verify hits the
    /// same unmeasurable source and errors, and the next refresh queues it
    /// again — an unbounded re-download loop. The removed SourceHash
    /// short-circuit used to be what stopped this repeating.
    /// </summary>
    [Fact]
    public void Equals_TagOnlySourceVsSizedLocal_IsNotADifference()
    {
        var src = Manifest(("Primary", 0, 0, 0, 0, "t"));
        var loc = Manifest(("Primary", 0, 5000, 100, 100, null));

        Assert.True(Cmp.Equals(src, loc));
        Assert.Null(Cmp.DescribeDifference(src, loc));
    }

    /// <summary>
    /// Losing sizes must not hide a change in image COUNT.
    /// True: a genuinely missing image is still queued while enrichment is down.
    /// False: the degraded path swallows real divergence.
    /// </summary>
    [Fact]
    public void Equals_TagOnlySource_StillDetectsCountMismatch()
    {
        var src = Manifest(("Primary", 0, 0, 0, 0, "t"), ("Primary", 1, 0, 0, 0, "t2"));
        var loc = Manifest(("Primary", 0, 5000, 100, 100, null));

        Assert.False(Cmp.Equals(src, loc));
    }

    /// <summary>
    /// Losing sizes must not hide a missing image TYPE.
    /// True: a type absent locally is still queued.
    /// False: the degraded path swallows real divergence.
    /// </summary>
    [Fact]
    public void Equals_TagOnlySource_StillDetectsMissingType()
    {
        var src = Manifest(("Primary", 0, 0, 0, 0, "t"), ("Backdrop", 0, 0, 0, 0, "b"));
        var loc = Manifest(("Primary", 0, 5000, 100, 100, null));

        Assert.False(Cmp.Equals(src, loc));
    }

    /// <summary>
    /// A measurable source against a missing or unreadable local file is still
    /// a difference, so the image gets re-pulled.
    /// True: a broken local image is repaired.
    /// False: missing local images are never restored.
    /// </summary>
    [Fact]
    public void Equals_SizedSourceVsZeroLocal_IsStillADifference()
    {
        var src = Manifest(("Primary", 0, 5000, 100, 100, "t"));
        var loc = Manifest(("Primary", 0, 0, 0, 0, null));

        Assert.False(Cmp.Equals(src, loc));
    }
}
