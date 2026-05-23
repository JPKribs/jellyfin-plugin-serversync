using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// String-array normalisation used by source/local blob builders.
/// </summary>
public static class StringNormalizationUtility
{
    /// <summary>
    /// Collapses null, empty array, and arrays containing only whitespace into a canonical null.
    /// Surviving entries are whitespace-filtered and case-insensitively sorted.
    /// </summary>
    public static string[]? NormalizeStringArray(IReadOnlyList<string>? source)
    {
        if (source == null)
        {
            return null;
        }

        var filtered = source
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return filtered.Length == 0 ? null : filtered;
    }
}
