using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Reads JSON fields while telling an absent field from one that is present and null, so a caller can skip the first and clear the second.
/// </summary>
public static class JsonFieldHelpers
{
    /// <summary>
    /// Reads a string field and passes it to <paramref name="assign"/>, skipping a field that is absent.
    /// </summary>
    /// <param name="metadata">The item's metadata fields by name.</param>
    /// <param name="key">The field name to read.</param>
    /// <param name="assign">Receives the value, which is null when the field is null, and returns whether it changed the item.</param>
    /// <returns><c>true</c> when <paramref name="assign"/> changed the item.</returns>
    public static bool AssignString(Dictionary<string, JsonElement> metadata, string key, Func<string?, bool> assign)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(assign);
        if (!metadata.TryGetValue(key, out var v)) return false;

        return assign(v.ValueKind == JsonValueKind.String ? v.GetString() : null);
    }

    /// <summary>
    /// Reads a float field and passes it to <paramref name="assign"/>, skipping a field that is absent.
    /// </summary>
    /// <param name="metadata">The item's metadata fields by name.</param>
    /// <param name="key">The field name to read.</param>
    /// <param name="assign">Receives the value, which is null when the field is null, and returns whether it changed the item.</param>
    /// <returns><c>true</c> when <paramref name="assign"/> changed the item.</returns>
    public static bool AssignFloat(Dictionary<string, JsonElement> metadata, string key, Func<float?, bool> assign)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(assign);
        if (!metadata.TryGetValue(key, out var v)) return false;

        float? read = v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;
        return assign(read);
    }

    /// <summary>
    /// Reads an integer field and passes it to <paramref name="assign"/>, skipping a field that is absent.
    /// </summary>
    /// <param name="metadata">The item's metadata fields by name.</param>
    /// <param name="key">The field name to read.</param>
    /// <param name="assign">Receives the value, which is null when the field is null, and returns whether it changed the item.</param>
    /// <returns><c>true</c> when <paramref name="assign"/> changed the item.</returns>
    public static bool AssignInt(Dictionary<string, JsonElement> metadata, string key, Func<int?, bool> assign)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(assign);
        if (!metadata.TryGetValue(key, out var v)) return false;

        int? read = v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        return assign(read);
    }

    /// <summary>
    /// Parses an ISO 8601 date, keeping the kind it was written with.
    /// </summary>
    /// <param name="v">The JSON value holding the date text.</param>
    /// <returns>The date, or null when the value is missing, not text, or not a date.</returns>
    public static DateTime? ParseNullableDate(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        if (string.IsNullOrEmpty(s)) return null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Reads a string array, dropping entries that are not strings.
    /// </summary>
    /// <param name="v">The JSON value holding the array.</param>
    /// <returns>The strings, or an empty array when the value is not an array.</returns>
    public static string[] ReadStringArray(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var entry in v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String))
        {
            var s = entry.GetString();
            if (s != null)
            {
                list.Add(s);
            }
        }

        return list.ToArray();
    }

    /// <summary>
    /// Reads an enum array, dropping entries that do not parse.
    /// </summary>
    /// <typeparam name="T">The enum to parse each entry as.</typeparam>
    /// <param name="v">The JSON value holding the array.</param>
    /// <returns>The values that parsed, or an empty array when the value is not an array.</returns>
    public static T[] ReadEnumArray<T>(JsonElement v) where T : struct, Enum
    {
        if (v.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<T>();
        }

        var list = new List<T>();
        foreach (var entry in v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String))
        {
            var s = entry.GetString();
            if (!string.IsNullOrEmpty(s) && Enum.TryParse<T>(s, out var parsed))
            {
                list.Add(parsed);
            }
        }

        return list.ToArray();
    }

    /// <summary>
    /// Whether a provider id is one of this plugin's own marks, such as the Identify pin or a mirrored playlist's tag.
    /// Each names something on this server only, so it is never compared, copied, or removed to match another server.
    /// </summary>
    /// <param name="key">The provider id's name.</param>
    /// <returns><c>true</c> for this plugin's own keys.</returns>
    public static bool IsOwnProviderKey(string? key)
        => key is not null && key.StartsWith("ServerSync", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a ProviderIds object as a dictionary that ignores case, dropping empty values.
    /// </summary>
    /// <param name="v">The JSON value holding the object.</param>
    /// <returns>The provider ids by provider name.</returns>
    public static Dictionary<string, string> ReadProviderIds(JsonElement v)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (v.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var prop in v.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String && !IsOwnProviderKey(p.Name)))
        {
            var pv = prop.Value.GetString();
            if (!string.IsNullOrEmpty(pv))
            {
                result[prop.Name] = pv;
            }
        }

        return result;
    }

    /// <summary>
    /// Reduces an item's provider ids to what is compared between servers, without empty values or this plugin's own keys.
    /// Keys that differ only by case count as one id, and the first in ordinal order is kept.
    /// </summary>
    /// <param name="ids">The ids as the item holds them.</param>
    /// <returns>The ids to compare, or null when there are none to read.</returns>
    public static Dictionary<string, string>? CollapseProviderIds(IEnumerable<KeyValuePair<string, string>>? ids)
        => ids?
            .Where(id => !string.IsNullOrEmpty(id.Value) && !IsOwnProviderKey(id.Key))
            .GroupBy(id => id.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(id => id.Key, StringComparer.Ordinal).First())
            .OrderBy(id => id.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(id => id.Key, id => id.Value);

    /// <summary>
    /// Makes an item's provider ids match the source's keys, spelling, and values, leaving this plugin's own keys alone.
    /// </summary>
    /// <param name="local">The item's provider ids, changed in place.</param>
    /// <param name="source">The source's provider ids.</param>
    /// <returns><c>true</c> when anything changed.</returns>
    public static bool ReconcileProviderIds(IDictionary<string, string> local, IReadOnlyDictionary<string, string> source)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(source);
        var changed = false;
        var spelled = new HashSet<string>(source.Keys, StringComparer.Ordinal);
        foreach (var key in local.Keys.Where(k => !spelled.Contains(k) && !IsOwnProviderKey(k)).ToList())
        {
            local.Remove(key);
            changed = true;
        }

        foreach (var id in source.Where(id => !local.TryGetValue(id.Key, out var held) || held != id.Value))
        {
            local[id.Key] = id.Value;
            changed = true;
        }

        return changed;
    }
}
