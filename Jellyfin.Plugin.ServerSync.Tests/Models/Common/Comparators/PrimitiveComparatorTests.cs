using System;
using System.Globalization;
using Jellyfin.Plugin.ServerSync.Models.Common.Comparators;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Models.Common.Comparators;

/// <summary>
/// A simple value equals only the same value, and its fingerprint is the same text on every server.
/// </summary>
public sealed class PrimitiveComparatorTests
{
    /// <summary>
    /// Two numbers compare by value, and a missing one equals only another missing one.
    /// True: a row whose source and local numbers match is not queued.
    /// False: equal values would queue forever, or a missing value would pass as a match.
    /// </summary>
    [Theory]
    [InlineData(5, 5, true)]
    [InlineData(5, 6, false)]
    [InlineData(null, null, true)]
    [InlineData(5, null, false)]
    [InlineData(null, 5, false)]
    [InlineData(int.MaxValue, int.MaxValue, true)]
    public void Equals_NullableNumbers_ComparesByValue(int? source, int? local, bool expected)
    {
        var comparator = new PrimitiveComparator<int?>();

        var result = comparator.Equals(source, local);

        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Text compares by ordinal value, so a change of case is a difference.
    /// True: a renamed value is seen as a change.
    /// False: a case only edit on the source would never reach this server.
    /// </summary>
    [Theory]
    [InlineData("a", "a", true)]
    [InlineData("a", "A", false)]
    [InlineData("", "", true)]
    [InlineData("", null, false)]
    public void Equals_Text_IsCaseSensitive(string? source, string? local, bool expected)
    {
        var comparator = new PrimitiveComparator<string>();

        var result = comparator.Equals(source, local);

        Assert.Equal(expected, result);
    }

    /// <summary>
    /// A missing value has no fingerprint.
    /// True: a row with nothing on the source stores no hash.
    /// False: a missing value would be stored as text and compared as if it were real.
    /// </summary>
    [Fact]
    public void ComputeHash_Null_ReturnsNull()
    {
        var comparator = new PrimitiveComparator<long?>();

        var hash = comparator.ComputeHash(null);

        Assert.Null(hash);
    }

    /// <summary>
    /// A number's fingerprint is its invariant text whatever the thread's culture is.
    /// True: two servers with different locales store the same fingerprint for the same value.
    /// False: a server with a comma decimal culture would see every value as changed.
    /// </summary>
    [Fact]
    public void ComputeHash_Number_UsesInvariantCultureText()
    {
        var comparator = new PrimitiveComparator<double>();
        var before = CultureInfo.CurrentCulture;
        string? hash;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            hash = comparator.ComputeHash(1234.5);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }

        Assert.Equal("1234.5", hash);
    }

    /// <summary>
    /// The largest number keeps every digit in its fingerprint.
    /// True: two large values that differ by one have different fingerprints.
    /// False: large sizes or tick counts would collide and a change would be missed.
    /// </summary>
    [Fact]
    public void ComputeHash_LargestNumber_KeepsEveryDigit()
    {
        var comparator = new PrimitiveComparator<long>();

        var hash = comparator.ComputeHash(long.MaxValue);

        Assert.Equal("9223372036854775807", hash);
    }

    /// <summary>
    /// A Guid and a piece of text each fingerprint as their own text.
    /// True: an id or a name is stored as written.
    /// False: ids would not match what another scan stored for the same value.
    /// </summary>
    [Fact]
    public void ComputeHash_GuidAndText_UseTheirOwnText()
    {
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

        var guidHash = new PrimitiveComparator<Guid>().ComputeHash(id);
        var textHash = new PrimitiveComparator<string>().ComputeHash("Name");

        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", guidHash);
        Assert.Equal("Name", textHash);
    }
}
