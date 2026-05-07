using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Read/assign helpers for the apply paths in <c>SyncMissing*Task.cs</c>
/// files. The pattern across modules is: deserialize a metadata blob into
/// <c>Dictionary&lt;string, JsonElement&gt;</c> and conditionally assign
/// each field. These helpers handle the "field absent → skip" vs
/// "field present and null → assign null/empty" distinction uniformly.
/// </summary>
public static class JsonFieldHelpers
{
    /// <summary>
    /// Reads a string-typed field and passes it to <paramref name="assign"/>.
    /// Returns whatever the assigner returns; returns false (and does not
    /// invoke the assigner) when the key is absent. JSON <c>null</c> /
    /// <c>undefined</c> / non-string kinds all read as <c>null</c> — the
    /// assigner decides whether to write null or skip.
    /// </summary>
    public static bool AssignString(Dictionary<string, JsonElement> metadata, string key, Func<string?, bool> assign)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(assign);
        if (!metadata.TryGetValue(key, out var v)) return false;

        string? read = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            _ => null
        };

        return assign(read);
    }

    /// <summary>Same shape as <see cref="AssignString"/> for nullable float values.</summary>
    public static bool AssignFloat(Dictionary<string, JsonElement> metadata, string key, Func<float?, bool> assign)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(assign);
        if (!metadata.TryGetValue(key, out var v)) return false;

        float? read = v.ValueKind == JsonValueKind.Number ? v.GetSingle() : (float?)null;
        return assign(read);
    }

    /// <summary>Same shape as <see cref="AssignString"/> for nullable int values.</summary>
    public static bool AssignInt(Dictionary<string, JsonElement> metadata, string key, Func<int?, bool> assign)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(assign);
        if (!metadata.TryGetValue(key, out var v)) return false;

        int? read = v.ValueKind == JsonValueKind.Number ? v.GetInt32() : (int?)null;
        return assign(read);
    }

    /// <summary>
    /// Parses a date string from a JsonElement. Returns null on missing or
    /// invalid input. Uses <see cref="DateTimeStyles.RoundtripKind"/> so
    /// timezone information in the source string is preserved.
    /// </summary>
    public static DateTime? ParseNullableDate(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        if (string.IsNullOrEmpty(s)) return null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Reads a string array from a JsonElement, dropping nulls. Returns an
    /// empty array (not null) when the input is missing or non-array.
    /// </summary>
    public static string[] ReadStringArray(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var entry in v.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var s = entry.GetString();
                if (s != null)
                {
                    list.Add(s);
                }
            }
        }

        return list.ToArray();
    }

    /// <summary>
    /// Reads an enum array from a JsonElement, dropping unparseable entries.
    /// Returns an empty array (not null) when the input is missing or non-array.
    /// </summary>
    public static T[] ReadEnumArray<T>(JsonElement v) where T : struct, Enum
    {
        if (v.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<T>();
        }

        var list = new List<T>();
        foreach (var entry in v.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var s = entry.GetString();
                if (!string.IsNullOrEmpty(s) && Enum.TryParse<T>(s, out var parsed))
                {
                    list.Add(parsed);
                }
            }
        }

        return list.ToArray();
    }

    /// <summary>
    /// Reads a Jellyfin-style ProviderIds object: keys are case-insensitive,
    /// values are stripped of empty strings.
    /// </summary>
    public static Dictionary<string, string> ReadProviderIds(JsonElement v)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (v.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var prop in v.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var pv = prop.Value.GetString();
                if (!string.IsNullOrEmpty(pv))
                {
                    result[prop.Name] = pv;
                }
            }
        }

        return result;
    }
}
