using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Utilities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

public class LocalImageUtilitiesTests
{
    /// <summary>
    /// Multiple backdrops keep their source order and get slots 0..n-1.
    /// True: SaveImage writes each backdrop into its own slot, so all of them survive the apply.
    /// False: out-of-order slots make SaveImage append then overwrite, collapsing the set to one image.
    /// </summary>
    [Fact]
    public void AssignSequentialSlots_MultipleBackdrops_NumbersSlotsInSourceOrder()
    {
        var work = new List<(string ImageType, int? ImageIndex)>
        {
            ("Backdrop", 2),
            ("Backdrop", 0),
            ("Backdrop", 1)
        };

        var ordered = LocalImageUtilities.AssignSequentialSlots(work);

        Assert.Equal(new[] { 0, 1, 2 }, ordered.Select(o => o.TargetIndex));
        Assert.Equal(new int?[] { 0, 1, 2 }, ordered.Select(o => o.SourceIndex));
    }

    /// <summary>
    /// Sparse source indexes are compacted to contiguous slots.
    /// True: the local manifest numbers images by position, so a gap-free target set verifies clean.
    /// False: a slot 5 written into an empty set lands at position 0 and the comparison never settles.
    /// </summary>
    [Fact]
    public void AssignSequentialSlots_SparseSourceIndexes_CompactsToContiguousSlots()
    {
        var work = new List<(string ImageType, int? ImageIndex)>
        {
            ("Backdrop", 5),
            ("Backdrop", 9)
        };

        var ordered = LocalImageUtilities.AssignSequentialSlots(work);

        Assert.Equal(new[] { 0, 1 }, ordered.Select(o => o.TargetIndex));
        Assert.Equal(new int?[] { 5, 9 }, ordered.Select(o => o.SourceIndex));
    }

    /// <summary>
    /// Null source indexes are treated as zero rather than dropped.
    /// True: a source that omits the index still gets its image downloaded and saved.
    /// False: a null index would sort unpredictably or throw, losing the image entirely.
    /// </summary>
    [Fact]
    public void AssignSequentialSlots_NullSourceIndex_SortsAsZeroAndKeepsTheEntry()
    {
        var work = new List<(string ImageType, int? ImageIndex)>
        {
            ("Backdrop", 1),
            ("Backdrop", null)
        };

        var ordered = LocalImageUtilities.AssignSequentialSlots(work);

        Assert.Equal(2, ordered.Count);
        Assert.Null(ordered[0].SourceIndex);
        Assert.Equal(0, ordered[0].TargetIndex);
        Assert.Equal(1, ordered[1].SourceIndex);
        Assert.Equal(1, ordered[1].TargetIndex);
    }

    /// <summary>
    /// Slot numbering restarts for each image type.
    /// True: Primary stays at slot 0 while backdrops number independently, matching how Jellyfin stores them.
    /// False: a shared counter would push Primary to slot 1 and orphan it.
    /// </summary>
    [Fact]
    public void AssignSequentialSlots_MixedTypes_NumbersEachTypeIndependently()
    {
        var work = new List<(string ImageType, int? ImageIndex)>
        {
            ("Backdrop", 0),
            ("Primary", 0),
            ("Backdrop", 1)
        };

        var ordered = LocalImageUtilities.AssignSequentialSlots(work);

        var backdrops = ordered.Where(o => o.ImageType == "Backdrop").ToList();
        var primary = Assert.Single(ordered, o => o.ImageType == "Primary");

        Assert.Equal(new[] { 0, 1 }, backdrops.Select(o => o.TargetIndex));
        Assert.Equal(0, primary.TargetIndex);
    }

    /// <summary>
    /// An empty work list produces an empty result instead of throwing.
    /// True: callers can hand over whatever the source returned without a guard.
    /// False: a throw here would abort an apply that simply had nothing to do.
    /// </summary>
    [Fact]
    public void AssignSequentialSlots_EmptyWork_ReturnsEmpty()
    {
        var ordered = LocalImageUtilities.AssignSequentialSlots(new List<(string, int?)>());

        Assert.Empty(ordered);
    }
}
