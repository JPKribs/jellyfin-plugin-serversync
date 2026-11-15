using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>
/// Compares JSON by meaning, ignoring property order and formatting.
/// </summary>
public static class JsonComparisonUtility
{
    /// <summary>
    /// Compares two JSON strings by meaning, ignoring property order and formatting.
    /// </summary>
    /// <param name="json1">The source's JSON.</param>
    /// <param name="json2">The JSON compared with it.</param>
    /// <returns><c>true</c> when the two mean the same.</returns>
    public static bool JsonEquals(string? json1, string? json2)
    {
        if (string.IsNullOrEmpty(json1) && string.IsNullOrEmpty(json2))
        {
            return true;
        }

        if (string.IsNullOrEmpty(json1) || string.IsNullOrEmpty(json2))
        {
            return false;
        }

        // Rows in step usually serialize identically, which skips the costly parse.
        if (string.Equals(json1, json2, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            using var doc1 = JsonDocument.Parse(json1);
            using var doc2 = JsonDocument.Parse(json2);

            return JsonElementEquals(doc1.RootElement, doc2.RootElement);
        }
        catch (JsonException)
        {
            return string.Equals(json1, json2, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Counts the top level properties that differ between two JSON objects. A property empty on both sides is not counted.
    /// </summary>
    /// <param name="json1">The source's JSON.</param>
    /// <param name="json2">The JSON compared with it.</param>
    /// <returns>How many properties differ.</returns>
    public static int CountDifferences(string? json1, string? json2)
    {
        if (string.IsNullOrEmpty(json1) || string.IsNullOrEmpty(json2))
        {
            return !string.IsNullOrEmpty(json1) || !string.IsNullOrEmpty(json2) ? 1 : 0;
        }

        try
        {
            using var doc1 = JsonDocument.Parse(json1);
            using var doc2 = JsonDocument.Parse(json2);

            var obj1 = doc1.RootElement;
            var obj2 = doc2.RootElement;

            if (obj1.ValueKind != JsonValueKind.Object || obj2.ValueKind != JsonValueKind.Object)
            {
                return JsonElementEquals(obj1, obj2) ? 0 : 1;
            }

            int diffCount = 0;

            var props1 = obj1.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
            var props2 = obj2.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

            var allKeys = new HashSet<string>(props1.Keys);
            allKeys.UnionWith(props2.Keys);

            foreach (var key in allKeys)
            {
                var has1 = props1.TryGetValue(key, out var val1);
                var has2 = props2.TryGetValue(key, out var val2);

                if (has1 && has2)
                {
                    if (!JsonElementEquals(val1, val2))
                    {
                        diffCount++;
                    }
                }
                else if (has1)
                {
                    // A property only one side holds differs only when it is not empty.
                    if (!IsEmptyValue(val1))
                    {
                        diffCount++;
                    }
                }
                else if (has2 && !IsEmptyValue(val2))
                {
                    diffCount++;
                }
            }

            return diffCount;
        }
        catch (JsonException)
        {
            return 1;
        }
    }

    /// <summary>
    /// Names the top level properties that differ between two JSON objects, treating null, empty, and missing as equal.
    /// </summary>
    /// <param name="json1">The source's JSON.</param>
    /// <param name="json2">The JSON compared with it.</param>
    /// <returns>The names of the fields that differ, sorted.</returns>
    public static IReadOnlyList<string> GetDifferingFields(string? json1, string? json2)
    {
        if (string.IsNullOrEmpty(json1) || string.IsNullOrEmpty(json2))
        {
            // A blob only one side holds has no fields to name.
            return !string.IsNullOrEmpty(json1) || !string.IsNullOrEmpty(json2)
                ? new[] { "(blob)" }
                : Array.Empty<string>();
        }

        try
        {
            using var doc1 = JsonDocument.Parse(json1);
            using var doc2 = JsonDocument.Parse(json2);

            var obj1 = doc1.RootElement;
            var obj2 = doc2.RootElement;

            if (obj1.ValueKind != JsonValueKind.Object || obj2.ValueKind != JsonValueKind.Object)
            {
                return JsonElementEquals(obj1, obj2) ? Array.Empty<string>() : new[] { "(root)" };
            }

            var props1 = obj1.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
            var props2 = obj2.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

            var allKeys = new HashSet<string>(props1.Keys);
            allKeys.UnionWith(props2.Keys);

            var diffs = new List<string>();
            foreach (var key in allKeys)
            {
                var has1 = props1.TryGetValue(key, out var val1);
                var has2 = props2.TryGetValue(key, out var val2);

                if (has1 && has2)
                {
                    if (!JsonElementEquals(val1, val2))
                    {
                        diffs.Add(key);
                    }
                }
                else if (has1)
                {
                    if (!IsEmptyValue(val1))
                    {
                        diffs.Add(key);
                    }
                }
                else if (has2 && !IsEmptyValue(val2))
                {
                    diffs.Add(key);
                }
            }

            diffs.Sort(StringComparer.Ordinal);
            return diffs;
        }
        catch (JsonException)
        {
            return new[] { "(parse-error)" };
        }
    }

    /// <summary>
    /// Describes the first few differing properties with both values, cut to fit a log line.
    /// </summary>
    /// <param name="json1">The source's JSON.</param>
    /// <param name="json2">The local JSON.</param>
    /// <param name="maxFields">The most fields to describe.</param>
    /// <param name="maxValueLength">The longest value to show before it is cut.</param>
    /// <returns>The differing fields with their values, in words.</returns>
    public static string DescribeDifferingFields(
        string? json1,
        string? json2,
        int maxFields = 3,
        int maxValueLength = 120)
    {
        var fields = GetDifferingFields(json1, json2);
        if (fields.Count == 0 || string.IsNullOrEmpty(json1) || string.IsNullOrEmpty(json2))
        {
            return string.Empty;
        }

        try
        {
            using var doc1 = JsonDocument.Parse(json1);
            using var doc2 = JsonDocument.Parse(json2);
            if (doc1.RootElement.ValueKind != JsonValueKind.Object
                || doc2.RootElement.ValueKind != JsonValueKind.Object)
            {
                return string.Join(",", fields);
            }

            var props1 = doc1.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            var props2 = doc2.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

            var pieces = new List<string>();
            foreach (var name in fields)
            {
                if (pieces.Count >= maxFields)
                {
                    pieces.Add($"+{fields.Count - pieces.Count} more");
                    break;
                }

                var s = props1.TryGetValue(name, out var v1) ? v1.GetRawText() : "<missing>";
                var l = props2.TryGetValue(name, out var v2) ? v2.GetRawText() : "<missing>";
                pieces.Add($"{name}: source={Truncate(s, maxValueLength)} local={Truncate(l, maxValueLength)}");
            }

            return string.Join("; ", pieces);
        }
        catch (JsonException)
        {
            return string.Join(",", fields);
        }
    }

    /// <summary>Cuts a text to a length, marking the cut with an ellipsis.</summary>
    private static string Truncate(string s, int max)
        => s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");

    /// <summary>
    /// Whether a string starts like an ISO 8601 date, which decides if a date parse is worth trying.
    /// </summary>
    private static bool LooksLikeIsoDate(string? s) =>
        s is { Length: >= 10 and <= 40 }
        && char.IsAsciiDigit(s[0])
        && char.IsAsciiDigit(s[1])
        && char.IsAsciiDigit(s[2])
        && char.IsAsciiDigit(s[3])
        && s[4] == '-';

    /// <summary>
    /// Whether an element is null, undefined, or an empty string, array, or object.
    /// </summary>
    private static bool IsEmptyValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null => true,
        JsonValueKind.Undefined => true,
        JsonValueKind.String => string.IsNullOrEmpty(e.GetString()),
        JsonValueKind.Array => e.GetArrayLength() == 0,
        JsonValueKind.Object => !e.EnumerateObject().Any(),
        _ => false
    };

    /// <summary>
    /// Compares two times as calendar dates, ignoring the time of day and the zone.
    /// </summary>
    /// <param name="a">The source's date.</param>
    /// <param name="b">The date compared with it.</param>
    /// <returns><c>true</c> when both fall on the same UTC calendar date.</returns>
    public static bool DateOnlyEquals(DateTime? a, DateTime? b)
    {
        if (!a.HasValue && !b.HasValue)
        {
            return true;
        }

        if (!a.HasValue || !b.HasValue)
        {
            return false;
        }

        // A time with no zone reads as UTC, as Jellyfin stores date fields.
        return JPKribs.Jellyfin.Base.UtcTime.AsUtc(a.Value).Date == JPKribs.Jellyfin.Base.UtcTime.AsUtc(b.Value).Date;
    }

    /// <summary>
    /// Formats a date field as its UTC calendar date, <c>yyyy-MM-dd</c>.
    /// Servers hold the same date at different times of day, so blobs store the date alone to agree with <see cref="DateOnlyEquals"/>.
    /// </summary>
    /// <param name="value">The time whose date is kept.</param>
    /// <returns>The date as text, or null when there is none.</returns>
    public static string? ToDateOnlyString(DateTime? value) =>
        value.HasValue
            ? JPKribs.Jellyfin.Base.UtcTime.AsUtc(value.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

    /// <summary>
    /// Formats a date field the SDK holds with an offset as its UTC calendar date, <c>yyyy-MM-dd</c>.
    /// </summary>
    /// <param name="value">The time whose date is kept.</param>
    /// <returns>The date as text, or null when there is none.</returns>
    public static string? ToDateOnlyString(DateTimeOffset? value) =>
        value.HasValue
            ? value.Value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

    /// <summary>
    /// Compares two elements in depth, treating null, undefined, and an empty string, array, or object as equal.
    /// </summary>
    /// <param name="e1">The source's element.</param>
    /// <param name="e2">The element compared with it.</param>
    /// <returns><c>true</c> when the two mean the same.</returns>
    public static bool JsonElementEquals(JsonElement e1, JsonElement e2)
    {
        if (IsEmptyValue(e1) && IsEmptyValue(e2))
        {
            return true;
        }

        if (IsEmptyValue(e1) || IsEmptyValue(e2))
        {
            return false;
        }

        if (e1.ValueKind != e2.ValueKind)
        {
            return false;
        }

        switch (e1.ValueKind)
        {
            case JsonValueKind.Object:
                var props1 = e1.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var props2 = e2.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

                var allKeys = new HashSet<string>(props1.Keys);
                allKeys.UnionWith(props2.Keys);

                foreach (var key in allKeys)
                {
                    var has1 = props1.TryGetValue(key, out var val1);
                    var has2 = props2.TryGetValue(key, out var val2);

                    if (has1 && has2)
                    {
                        if (!JsonElementEquals(val1, val2))
                        {
                            return false;
                        }
                    }
                    else if (has1)
                    {
                        // A property only one side holds differs only when it is not empty.
                        if (!IsEmptyValue(val1))
                        {
                            return false;
                        }
                    }
                    else if (has2 && !IsEmptyValue(val2))
                    {
                        return false;
                    }
                }

                return true;

            case JsonValueKind.Array:
                var arr1 = e1.EnumerateArray().ToList();
                var arr2 = e2.EnumerateArray().ToList();

                if (arr1.Count != arr2.Count)
                {
                    return false;
                }

                for (int i = 0; i < arr1.Count; i++)
                {
                    if (!JsonElementEquals(arr1[i], arr2[i]))
                    {
                        return false;
                    }
                }

                return true;

            case JsonValueKind.String:
                var s1 = e1.GetString();
                var s2 = e2.GetString();

                if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2))
                {
                    return true;
                }

                if (s1 == s2)
                {
                    return true;
                }

                // Only strings shaped like dates are parsed, since parsing every unequal pair is costly.
                if (LooksLikeIsoDate(s1) && LooksLikeIsoDate(s2) &&
                    DateTimeOffset.TryParse(s1, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto1) &&
                    DateTimeOffset.TryParse(s2, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto2))
                {
                    if (dto1.UtcDateTime == dto2.UtcDateTime)
                    {
                        return true;
                    }

                    // Two midnights in their own zones on one calendar date are one date.
                    if (dto1.TimeOfDay == TimeSpan.Zero
                        && dto2.TimeOfDay == TimeSpan.Zero
                        && dto1.Date == dto2.Date)
                    {
                        return true;
                    }

                    // A midnight shifted by a zone lands on a whole hour that date.
                    if (dto1.Minute == 0 && dto1.Second == 0
                        && dto2.Minute == 0 && dto2.Second == 0
                        && dto1.UtcDateTime.Date == dto2.UtcDateTime.Date
                        && Math.Abs((dto1.UtcDateTime - dto2.UtcDateTime).TotalHours) <= 24)
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Number:
                // Numbers compare by value, so 1.0 equals 1.
                if (e1.TryGetInt64(out var l1) && e2.TryGetInt64(out var l2))
                {
                    return l1 == l2;
                }

                return e1.TryGetDouble(out var d1) && e2.TryGetDouble(out var d2) && d1.Equals(d2);

            case JsonValueKind.True:
            case JsonValueKind.False:
                return e1.GetBoolean() == e2.GetBoolean();

            default:
                return e1.GetRawText() == e2.GetRawText();
        }
    }
}
