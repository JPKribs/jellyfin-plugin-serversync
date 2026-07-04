using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>
/// Utility class for semantic JSON comparison.
/// Used across sync modules for comparing configuration values.
/// </summary>
public static class JsonComparisonUtility
{
    /// <summary>
    /// Compares two JSON strings for semantic equality.
    /// Handles differences in property ordering and formatting.
    /// </summary>
    /// <param name="json1">First JSON string.</param>
    /// <param name="json2">Second JSON string.</param>
    /// <returns>True if semantically equal, false otherwise.</returns>
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
    /// Counts the number of differing properties between two JSON objects.
    /// Properties where both values are "empty" (null, empty string, empty array) are not counted.
    /// </summary>
    /// <param name="json1">First JSON string.</param>
    /// <param name="json2">Second JSON string.</param>
    /// <returns>Number of differing properties.</returns>
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
                    // One-sided property counts only when its value is non-empty.
                    if (!IsEmptyValue(val1))
                    {
                        diffCount++;
                    }
                }
                else if (has2)
                {
                    if (!IsEmptyValue(val2))
                    {
                        diffCount++;
                    }
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
    /// Returns the names of top-level properties that differ between two
    /// JSON objects, with the same null/empty/missing equivalence as
    /// <see cref="JsonElementEquals"/>.
    /// </summary>
    public static IReadOnlyList<string> GetDifferingFields(string? json1, string? json2)
    {
        if (string.IsNullOrEmpty(json1) || string.IsNullOrEmpty(json2))
        {
            // Mismatched presence — not a property-level diff, but report it
            // distinctly so callers can still surface a useful message.
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
                else if (has2)
                {
                    if (!IsEmptyValue(val2))
                    {
                        diffs.Add(key);
                    }
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
    /// Returns a compact "field=source-vs-local" diagnostic for the first
    /// few divergent properties, with values JSON-serialised and truncated
    /// to fit in a log line.
    /// </summary>
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

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");

    /// <summary>
    /// Checks if a JsonElement represents an "empty" value (null, empty string, empty array, empty object).
    /// </summary>
    private static bool IsEmptyValue(JsonElement e)
    {
        return e.ValueKind switch
        {
            JsonValueKind.Null => true,
            JsonValueKind.Undefined => true,
            JsonValueKind.String => string.IsNullOrEmpty(e.GetString()),
            JsonValueKind.Array => e.GetArrayLength() == 0,
            JsonValueKind.Object => !e.EnumerateObject().Any(),
            _ => false
        };
    }

    /// <summary>
    /// Compares two nullable DateTime values as date-only (calendar date),
    /// ignoring time-of-day and Kind/timezone differences. Use for date-only
    /// semantic fields like birthdays, premiere dates, and end dates.
    /// </summary>
    /// <param name="a">First value.</param>
    /// <param name="b">Second value.</param>
    /// <returns>True if equal as calendar dates, false otherwise.</returns>
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

        return AsUtcSafe(a.Value).Date == AsUtcSafe(b.Value).Date;
    }

    // DateTime.ToUniversalTime() on a Kind=Unspecified value silently interprets it
    // as local time and shifts. We treat Unspecified as already-UTC instead, which
    // matches how Jellyfin commonly stores date-only fields.
    private static DateTime AsUtcSafe(DateTime dt) =>
        dt.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
            : dt.ToUniversalTime();

    /// <summary>
    /// Normalizes a date-only semantic field (premiere/end/birth/death dates)
    /// to its UTC calendar date as <c>yyyy-MM-dd</c>. Blob builders store
    /// this instead of the raw timestamp so the JSON comparison agrees with
    /// the apply step's <see cref="DateOnlyEquals"/> — servers routinely hold
    /// the same date with different times of day (TZ-shifted midnights,
    /// provider quirks like 07:01), and comparing timestamps left rows
    /// permanently diverged that the apply step correctly refused to touch.
    /// </summary>
    public static string? ToDateOnlyString(DateTime? value) =>
        value.HasValue
            ? AsUtcSafe(value.Value).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : null;

    /// <summary>
    /// <see cref="ToDateOnlyString(DateTime?)"/> for the SDK's offset-typed dates.
    /// </summary>
    public static string? ToDateOnlyString(DateTimeOffset? value) =>
        value.HasValue
            ? value.Value.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : null;

    /// <summary>
    /// Recursively compares two JsonElements for equality.
    /// Treats null, undefined, empty string, empty array, and empty object as equivalent.
    /// </summary>
    /// <param name="e1">First element.</param>
    /// <param name="e2">Second element.</param>
    /// <returns>True if equal, false otherwise.</returns>
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
                        // One-sided property is a diff only when non-empty.
                        if (!IsEmptyValue(val1))
                        {
                            return false;
                        }
                    }
                    else if (has2)
                    {
                        if (!IsEmptyValue(val2))
                        {
                            return false;
                        }
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

                // Compare as dates via DateTimeOffset to avoid the DateTime
                // Unspecified-kind TZ shift. AssumeUniversal makes offset-less
                // strings UTC.
                if (System.DateTimeOffset.TryParse(s1, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var dto1) &&
                    System.DateTimeOffset.TryParse(s2, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var dto2))
                {
                    if (dto1.UtcDateTime == dto2.UtcDateTime)
                    {
                        return true;
                    }

                    // Date-only relaxation: both midnight in their own offset
                    // with the same calendar date (PremiereDate / birthdays).
                    if (dto1.TimeOfDay == System.TimeSpan.Zero
                        && dto2.TimeOfDay == System.TimeSpan.Zero
                        && dto1.Date == dto2.Date)
                    {
                        return true;
                    }

                    // Whole-hour relaxation: TZ-shifted "midnight in local TZ"
                    // values land on whole-hour boundaries with the same UTC
                    // calendar date within a 24-hour window. Treat as equal.
                    if (dto1.Minute == 0 && dto1.Second == 0
                        && dto2.Minute == 0 && dto2.Second == 0
                        && dto1.UtcDateTime.Date == dto2.UtcDateTime.Date
                        && System.Math.Abs((dto1.UtcDateTime - dto2.UtcDateTime).TotalHours) <= 24)
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Number:
                // Compare numbers semantically rather than by raw text
                // (e.g., 1.0 and 1 should be considered equal)
                if (e1.TryGetInt64(out var l1) && e2.TryGetInt64(out var l2))
                {
                    return l1 == l2;
                }

                return e1.TryGetDouble(out var d1) && e2.TryGetDouble(out var d2) && d1.Equals(d2);

            case JsonValueKind.True:
            case JsonValueKind.False:
                return e1.GetBoolean() == e2.GetBoolean();

            case JsonValueKind.Null:
                return true;

            default:
                return e1.GetRawText() == e2.GetRawText();
        }
    }
}
