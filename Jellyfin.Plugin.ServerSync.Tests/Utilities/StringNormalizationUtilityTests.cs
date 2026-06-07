using Jellyfin.Plugin.ServerSync.Utilities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

public class StringNormalizationUtilityTests
{
    /// <summary>
    /// Null input returns null.
    /// True: source/local both report "no value" identically.
    /// False: divergent null-handling would create permanent false-positive diffs.
    /// </summary>
    [Fact]
    public void NormalizeStringArray_Null_ReturnsNull()
    {
        Assert.Null(StringNormalizationUtility.NormalizeStringArray(null));
    }

    /// <summary>
    /// Empty array returns null.
    /// True: [] collapses to null so the canonical "no value" shape is null.
    /// False: [] vs null would diff against each other on every refresh.
    /// </summary>
    [Fact]
    public void NormalizeStringArray_Empty_ReturnsNull()
    {
        Assert.Null(StringNormalizationUtility.NormalizeStringArray(System.Array.Empty<string>()));
    }

    /// <summary>
    /// Array of only whitespace/empty strings returns null.
    /// True: [""] (what Jellyfin sometimes persists for what we wrote as []) collapses to null.
    /// False: [""] would diff against null forever, pinning rows to Errored.
    /// </summary>
    [Fact]
    public void NormalizeStringArray_OnlyWhitespace_ReturnsNull()
    {
        Assert.Null(StringNormalizationUtility.NormalizeStringArray(new[] { string.Empty, "  ", "\t" }));
    }

    /// <summary>
    /// Mixed whitespace and real values drops the whitespace.
    /// True: only meaningful values survive normalisation on both sides.
    /// False: whitespace leakage would create asymmetric diffs.
    /// </summary>
    [Fact]
    public void NormalizeStringArray_DropsWhitespaceEntries()
    {
        var result = StringNormalizationUtility.NormalizeStringArray(new[] { "Drama", "  ", "Action", string.Empty });

        Assert.NotNull(result);
        Assert.Equal(new[] { "Action", "Drama" }, result);
    }

    /// <summary>
    /// Values are sorted case-insensitively.
    /// True: source and local serialise in the same canonical order regardless of input order.
    /// False: input-order differences would diff on every refresh.
    /// </summary>
    [Fact]
    public void NormalizeStringArray_SortsCaseInsensitively()
    {
        var result = StringNormalizationUtility.NormalizeStringArray(new[] { "zebra", "Apple", "monkey" });

        Assert.NotNull(result);
        Assert.Equal(new[] { "Apple", "monkey", "zebra" }, result);
    }

    /// <summary>
    /// Single value passes through.
    /// True: single-item arrays survive normalisation as a single-item array.
    /// False: single values would be inadvertently collapsed to null.
    /// </summary>
    [Fact]
    public void NormalizeStringArray_SingleValue_Survives()
    {
        var result = StringNormalizationUtility.NormalizeStringArray(new[] { "Drama" });

        Assert.NotNull(result);
        Assert.Equal(new[] { "Drama" }, result);
    }
}
