using System;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Common;

public class JsonComparisonUtilityTests
{
    /// <summary>
    /// Both inputs null or empty compare equal.
    /// True: empty/missing sides are treated as the no-op match case.
    /// False: empty rows would diff against themselves and queue noise.
    /// </summary>
    [Fact]
    public void JsonEquals_BothNullOrEmpty_IsTrue()
    {
        Assert.True(JsonComparisonUtility.JsonEquals(null, null));
        Assert.True(JsonComparisonUtility.JsonEquals(string.Empty, null));
        Assert.True(JsonComparisonUtility.JsonEquals(null, string.Empty));
        Assert.True(JsonComparisonUtility.JsonEquals(string.Empty, string.Empty));
    }

    /// <summary>
    /// Object equality is independent of key ordering.
    /// True: source and local serialised by independent code paths still compare equal.
    /// False: every refresh would diff forever because key order is implementation-defined.
    /// </summary>
    [Fact]
    public void JsonEquals_KeyOrderInsensitive()
    {
        Assert.True(JsonComparisonUtility.JsonEquals(
            "{\"a\":1,\"b\":2,\"c\":3}",
            "{\"c\":3,\"a\":1,\"b\":2}"));
    }

    /// <summary>
    /// Nested object equality is also order-insensitive.
    /// True: nested structures (Policy/Config sub-objects) compare semantically.
    /// False: nested objects would diff on every refresh.
    /// </summary>
    [Fact]
    public void JsonEquals_NestedObjects_KeyOrderInsensitive()
    {
        Assert.True(JsonComparisonUtility.JsonEquals(
            "{\"outer\":{\"a\":1,\"b\":2}}",
            "{\"outer\":{\"b\":2,\"a\":1}}"));
    }

    /// <summary>
    /// Different values on the same key compare not-equal.
    /// True: real value diffs are surfaced.
    /// False: divergent records would stay Synced.
    /// </summary>
    [Fact]
    public void JsonEquals_DistinguishesValues()
    {
        Assert.False(JsonComparisonUtility.JsonEquals(
            "{\"a\":1}",
            "{\"a\":2}"));
    }

    /// <summary>
    /// Arrays compare positionally, not as sets.
    /// True: callers know they must sort arrays explicitly (PeopleSync sorts cast lists, for example).
    /// False: silently treating arrays as sets would hide real ordering differences from callers.
    /// </summary>
    [Fact]
    public void JsonEquals_ArrayOrderMatters()
    {
        Assert.False(JsonComparisonUtility.JsonEquals("[1,2,3]", "[3,2,1]"));
    }

    /// <summary>
    /// Non-JSON inputs fall back to string comparison.
    /// True: malformed values still compare in a predictable way without throwing.
    /// False: a JsonException would crash the refresh pass on any malformed blob.
    /// </summary>
    [Fact]
    public void JsonEquals_InvalidJson_FallsBackToStringCompare()
    {
        Assert.True(JsonComparisonUtility.JsonEquals("not-json", "not-json"));
        Assert.False(JsonComparisonUtility.JsonEquals("not-json", "other-not-json"));
    }

    /// <summary>
    /// Identical objects produce zero differences.
    /// True: ChangesSummary reports "No changes" for synced rows.
    /// False: spurious diff counts inflate the changes summary on Synced rows.
    /// </summary>
    [Fact]
    public void CountDifferences_ZeroForIdenticalObjects()
    {
        Assert.Equal(0, JsonComparisonUtility.CountDifferences(
            "{\"a\":1,\"b\":2}",
            "{\"a\":1,\"b\":2}"));
    }

    /// <summary>
    /// One field change produces a count of 1.
    /// True: UI displays "1 difference" for a single-field divergence.
    /// False: incorrect counts mislead operators when triaging diffs.
    /// </summary>
    [Fact]
    public void CountDifferences_OneForSingleFieldChange()
    {
        Assert.Equal(1, JsonComparisonUtility.CountDifferences(
            "{\"a\":1,\"b\":2}",
            "{\"a\":1,\"b\":3}"));
    }

    /// <summary>
    /// Missing fields on one side count as a difference.
    /// True: schema differences are reported, not silently ignored.
    /// False: structural divergence (e.g. deleted properties) would never surface in the UI.
    /// </summary>
    [Fact]
    public void CountDifferences_HandlesMissingField()
    {
        Assert.Equal(1, JsonComparisonUtility.CountDifferences(
            "{\"a\":1,\"b\":2}",
            "{\"a\":1}"));
    }

    /// <summary>
    /// GetDifferingFields names the specific fields that diverge.
    /// True: callers can list per-field diffs in logs and modals.
    /// False: only counts available — no actionable per-field info.
    /// </summary>
    [Fact]
    public void GetDifferingFields_ReturnsFieldNames()
    {
        var diffs = JsonComparisonUtility.GetDifferingFields(
            "{\"a\":1,\"b\":2,\"c\":3}",
            "{\"a\":1,\"b\":99,\"c\":3}");

        Assert.Single(diffs);
        Assert.Contains("b", diffs);
    }

    /// <summary>
    /// Identical objects return an empty diff list.
    /// True: no false positives on Synced rows.
    /// False: extra fields named on synced rows would confuse the UI.
    /// </summary>
    [Fact]
    public void GetDifferingFields_EmptyForIdenticalObjects()
    {
        var diffs = JsonComparisonUtility.GetDifferingFields(
            "{\"a\":1}",
            "{\"a\":1}");

        Assert.Empty(diffs);
    }

    /// <summary>
    /// Two same-date timestamps with different times-of-day compare equal date-only.
    /// True: round-trip drift in PremiereDate / EndDate doesn't trigger spurious diffs.
    /// False: every Refresh would falsely re-queue rows on dates that haven't actually changed.
    /// </summary>
    [Fact]
    public void DateOnlyEquals_SameDate_DifferentTimeOfDay_IsTrue()
    {
        var a = new DateTime(2025, 5, 23, 10, 30, 0, DateTimeKind.Utc);
        var b = new DateTime(2025, 5, 23, 16, 45, 12, DateTimeKind.Utc);

        Assert.True(JsonComparisonUtility.DateOnlyEquals(a, b));
    }

    /// <summary>
    /// Different calendar dates compare not-equal.
    /// True: a real date move (next-day premiere correction) is correctly detected.
    /// False: date changes would silently never sync.
    /// </summary>
    [Fact]
    public void DateOnlyEquals_DifferentDays_IsFalse()
    {
        var a = new DateTime(2025, 5, 23, 10, 0, 0, DateTimeKind.Utc);
        var b = new DateTime(2025, 5, 24, 10, 0, 0, DateTimeKind.Utc);

        Assert.False(JsonComparisonUtility.DateOnlyEquals(a, b));
    }

    /// <summary>
    /// Both null compare equal.
    /// True: unset dates on both sides are the no-op match case.
    /// False: null != null comparisons would queue rows without any date set.
    /// </summary>
    [Fact]
    public void DateOnlyEquals_BothNull_IsTrue()
    {
        Assert.True(JsonComparisonUtility.DateOnlyEquals(null, null));
    }

    /// <summary>
    /// Set vs unset date compares not-equal in either direction.
    /// True: dates being added or cleared are detectable.
    /// False: set/unset transitions would never sync.
    /// </summary>
    [Fact]
    public void DateOnlyEquals_OneNull_IsFalse()
    {
        Assert.False(JsonComparisonUtility.DateOnlyEquals(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), null));
        Assert.False(JsonComparisonUtility.DateOnlyEquals(null, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }
}
