using Jellyfin.Plugin.ServerSync.Models.Common.Comparators;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Common;

public class JsonBlobComparatorTests
{
    private static readonly JsonBlobComparator Cmp = new();

    /// <summary>
    /// Null and empty input produce a null hash.
    /// True: empty values can be detected by callers via a null check on SourceHash.
    /// False: empty values getting a non-null hash would mark "no data yet" rows as Synced.
    /// </summary>
    [Fact]
    public void ComputeHash_ReturnsNullForNullOrEmpty()
    {
        Assert.Null(Cmp.ComputeHash(null));
        Assert.Null(Cmp.ComputeHash(string.Empty));
    }

    /// <summary>
    /// Same JSON input produces the same hash across calls.
    /// True: stored SyncedHash will reliably match a re-extracted SourceHash on the next refresh.
    /// False: non-deterministic hashing breaks the SourceHash == SyncedHash short-circuit.
    /// </summary>
    [Fact]
    public void ComputeHash_StableForSameInput()
    {
        var a = Cmp.ComputeHash("{\"x\":1}");
        var b = Cmp.ComputeHash("{\"x\":1}");

        Assert.NotNull(a);
        Assert.Equal(a, b);
    }

    /// <summary>
    /// Different JSON content yields different hashes.
    /// True: changed source content is detectable, so re-sync is triggered.
    /// False: collisions mask real changes — rows stay Synced even after source diverges.
    /// </summary>
    [Fact]
    public void ComputeHash_DiffersForDifferentJson()
    {
        var a = Cmp.ComputeHash("{\"x\":1}");
        var b = Cmp.ComputeHash("{\"x\":2}");

        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// Hash is full 64-character lowercase SHA256 hex.
    /// True: hash format matches what the v21 migration cleared from UserSync.SyncedValueHash.
    /// False: format mismatch would silently invalidate the fast path after upgrade.
    /// </summary>
    [Fact]
    public void ComputeHash_ProducesLowercaseHexFullSha256()
    {
        var hash = Cmp.ComputeHash("{\"x\":1}");

        Assert.NotNull(hash);
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    /// <summary>
    /// Same keys in different order are semantically equal.
    /// True: source and local can be serialised independently and still compare equal.
    /// False: every row would diff forever — key ordering is not stable across serialisers.
    /// </summary>
    [Fact]
    public void Equals_TreatsKeyOrderingAsSemanticallyEqual()
    {
        Assert.True(Cmp.Equals("{\"a\":1,\"b\":2}", "{\"b\":2,\"a\":1}"));
    }

    /// <summary>
    /// Different values on the same key compare not-equal.
    /// True: real value diffs are detected, triggering re-sync.
    /// False: divergent values would appear equal and stay Synced.
    /// </summary>
    [Fact]
    public void Equals_DistinguishesDifferentValues()
    {
        Assert.False(Cmp.Equals("{\"a\":1}", "{\"a\":2}"));
    }

    /// <summary>
    /// Both null compare equal.
    /// True: "no data" on both sides is the no-op case and matches.
    /// False: null != null comparisons would queue empty rows for sync.
    /// </summary>
    [Fact]
    public void Equals_HandlesBothNull()
    {
        Assert.True(Cmp.Equals(null, null));
    }

    /// <summary>
    /// One side null and other side populated compare not-equal without throwing.
    /// True: comparator survives mixed null/non-null inputs and reports them as diff.
    /// False: NullReferenceException at runtime crashing the refresh.
    /// </summary>
    [Fact]
    public void Equals_HandlesOneNull()
    {
        var result = Cmp.Equals(null, "{\"a\":1}");
        Assert.False(result);
    }
}
