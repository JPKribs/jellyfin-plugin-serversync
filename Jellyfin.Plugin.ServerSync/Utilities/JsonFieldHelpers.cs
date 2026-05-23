using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Read helpers that distinguish "field absent" from "field present and null"
/// so apply-path callers can skip vs write a clearing value.
/// </summary>
public static class JsonFieldHelpers
{
    /// <summary>
    /// Reads a string field. Skips when the key is absent; otherwise passes the value
    /// (which may be null) to <paramref name="assign"/>.
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

    /// <summary>
    /// Reads a nullable float field. Same skip-when-absent semantics as <see cref="AssignString"/>.
    /// </summary>
    public static bool AssignFloat(Dictionary<string, JsonElement> metadata, string key, Func<float?, bool> assign)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(assign);
        if (!metadata.TryGetValue(key, out var v)) return false;

        float? read = v.ValueKind == JsonValueKind.Number ? v.GetSingle() : (float?)null;
        return assign(read);
    }

    /// <summary>
    /// Reads a nullable int field. Same skip-when-absent semantics as <see cref="AssignString"/>.
    /// </summary>
    public static bool AssignInt(Dictionary<string, JsonElement> metadata, string key, Func<int?, bool> assign)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(assign);
        if (!metadata.TryGetValue(key, out var v)) return false;

        int? read = v.ValueKind == JsonValueKind.Number ? v.GetInt32() : (int?)null;
        return assign(read);
    }

    /// <summary>
    /// Parses an ISO 8601 date string with <see cref="DateTimeStyles.RoundtripKind"/>.
    /// Returns null on missing, non-string, or unparseable input.
    /// </summary>
    public static DateTime? ParseNullableDate(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        if (string.IsNullOrEmpty(s)) return null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Reads a string array. Returns an empty array for missing or non-array input;
    /// drops non-string entries.
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
    /// Reads an enum array. Returns an empty array for missing or non-array input;
    /// drops unparseable entries.
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
    /// Reads a Jellyfin-style ProviderIds object as a case-insensitive string dictionary.
    /// Empty-string values are dropped.
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
