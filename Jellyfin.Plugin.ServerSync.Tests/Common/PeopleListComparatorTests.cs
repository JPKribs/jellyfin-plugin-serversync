using Jellyfin.Plugin.ServerSync.Models.Common.Comparators;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Common;

public class PeopleListComparatorTests
{
    private static readonly PeopleListComparator Cmp = new();

    /// <summary>
    /// Casing and padding differences are not a difference.
    /// True: Jellyfin matches credits without case and keeps the stored text, so an apply can never change them.
    /// False: the row verifies, then re-queues on the very next refresh, forever.
    /// </summary>
    [Fact]
    public void Equals_CaseAndWhitespaceOnly_AreEqual()
    {
        var source = "[{\"Name\":\"Jean-Claude van Damme \",\"Role\":\"Self\",\"Type\":\"Actor\"}]";
        var local = "[{\"Name\":\"Jean-Claude Van Damme\",\"Role\":\"self\",\"Type\":\"Actor\"}]";

        Assert.True(Cmp.Equals(source, local));
    }

    /// <summary>
    /// Duplicate credits collapse the way Jellyfin collapses them on write.
    /// </summary>
    [Fact]
    public void Equals_DuplicateSourceCredit_MatchesDedupedLocal()
    {
        var source = "[{\"Name\":\"A\",\"Type\":\"Actor\"},{\"Name\":\"a\",\"Type\":\"Actor\"},{\"Name\":\"B\",\"Type\":\"Director\"}]";
        var local = "[{\"Name\":\"A\",\"Type\":\"Actor\"},{\"Name\":\"B\",\"Type\":\"Director\"}]";

        Assert.True(Cmp.Equals(source, local));
    }

    /// <summary>
    /// A missing Type reads as Unknown, which is what the local side always writes.
    /// </summary>
    [Fact]
    public void Equals_MissingTypeVsUnknown_AreEqual()
    {
        Assert.True(Cmp.Equals("[{\"Name\":\"A\"}]", "[{\"Name\":\"A\",\"Type\":\"Unknown\"}]"));
    }

    [Fact]
    public void Equals_DifferentRole_IsDifferent()
    {
        var source = "[{\"Name\":\"A\",\"Role\":\"Hero\",\"Type\":\"Actor\"}]";
        var local = "[{\"Name\":\"A\",\"Role\":\"Villain\",\"Type\":\"Actor\"}]";

        Assert.False(Cmp.Equals(source, local));
        Assert.Contains("credit [0]", Cmp.DescribeDifference(source, local));
    }

    /// <summary>
    /// Billing order is part of the comparison so a reordered cast syncs.
    /// </summary>
    [Fact]
    public void Equals_DifferentOrder_IsDifferent()
    {
        var source = "[{\"Name\":\"A\",\"Type\":\"Actor\"},{\"Name\":\"B\",\"Type\":\"Actor\"}]";
        var local = "[{\"Name\":\"B\",\"Type\":\"Actor\"},{\"Name\":\"A\",\"Type\":\"Actor\"}]";

        Assert.False(Cmp.Equals(source, local));
    }

    [Fact]
    public void Equals_EmptyVariants_AreEqual()
    {
        Assert.True(Cmp.Equals("[]", null));
        Assert.True(Cmp.Equals(null, "[]"));
        Assert.Null(Cmp.ComputeHash("[]"));
    }

    [Fact]
    public void ComputeHash_IgnoresCase()
    {
        Assert.Equal(
            Cmp.ComputeHash("[{\"Name\":\"A\",\"Type\":\"Actor\"}]"),
            Cmp.ComputeHash("[{\"Name\":\"a\",\"Type\":\"actor\"}]"));
    }
}
