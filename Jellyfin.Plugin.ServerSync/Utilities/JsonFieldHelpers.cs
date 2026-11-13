using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Read helpers that distinguish "field absent" from "field present and null"
/// so apply-path callers can skip vs write a clearing value.
/// </summary>
public static class JsonFieldHelpers
{
    /// <summary>
    /// Reads a string field. Skips when the key is absent. Otherwise passes the value
    /// (which may be null) to <paramref name="assign"/>.
    /// </summary>
    public static bool AssignString(Dictionary<string, JsonElement> metadata, string key, Func<string?, bool> assign)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(assign);
        if (!metadata.TryGetValue(key, out var v)) return false;

        return assign(v.ValueKind == JsonValueKind.String ? v.GetString() : null);
    }

    /// <summary>
    /// Reads a nullable float field. Same skip-when-absent semantics as <see cref="AssignString"/>.
    /// </summary>
    public static bool AssignFloat(Dictionary<string, JsonElement> metadata, string key, Func<float?, bool> assign)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(assign);
        if (!metadata.TryGetValue(key, out var v)) return false;

        float? read = v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;
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

        int? read = v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
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
    /// Reads a string array. Returns an empty array for missing or non-array input,
    /// and drops non-string entries.
    /// </summary>
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
    /// Reads an enum array. Returns an empty array for missing or non-array input,
    /// and drops unparseable entries.
    /// </summary>
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
    /// Whether a provider id is one of this plugin's own marks on an item: the Identify pin, and the
    /// tags on mirrored playlists and collections. Each names something on this server only, so it is
    /// never compared with, copied from, or removed to match another server.
    /// </summary>
    /// <param name="key">The provider id's name.</param>
    /// <returns>True for this plugin's own keys.</returns>
    public static bool IsOwnProviderKey(string? key)
        => key is not null && key.StartsWith("ServerSync", StringComparison.OrdinalIgnoreCase);

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
    /// The provider ids of an item as they are compared between servers: without empty values or this
    /// plugin's own keys, sorted, and with keys that differ only by case taken as one id. Jellyfin
    /// stores provider ids by exact name, so an item can hold "TvdbCollection" and "tvdbcollection"
    /// side by side, which is one id written twice and not a difference from a server that holds it
    /// once. Of such keys the first in ordinal order is kept, on both servers alike.
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
    /// Makes an item's provider ids match the source's: the same keys, spelled the same, with the same
    /// values. A local key that differs from a source key only by case is removed, so the id is left
    /// once under the source's spelling. This plugin's own keys are left alone.
    /// </summary>
    /// <param name="local">The item's provider ids, changed in place.</param>
    /// <param name="source">The source's provider ids.</param>
    /// <returns>True when anything changed.</returns>
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
