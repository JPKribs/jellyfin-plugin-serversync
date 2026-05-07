using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Helpers for normalizing strings/arrays so source-side and local-side
/// blobs serialize identically across the comparator and the
/// JSON-equals path.
/// </summary>
public static class StringNormalizationUtility
{
    /// <summary>
    /// Collapses the three storage shapes Jellyfin returns for "no value"
    /// string arrays — <c>null</c>, <c>[]</c>, and <c>[""]</c> — into a
    /// single canonical <c>null</c>. Without this, an item whose source
    /// has e.g. <c>Tags: null</c> but whose local persists as <c>[""]</c>
    /// (empty-string-in-array, observed after UpdateToRepositoryAsync
    /// rounds our <c>[]</c> writes) compares unequal forever and
    /// verification keeps Erroring the row. Filtering whitespace and
    /// emitting null when empty makes both sides round-trip identically.
    /// </summary>
    /// <param name="source">The raw collection from Jellyfin.</param>
    /// <returns>Sorted, whitespace-filtered array, or null if the result is empty.</returns>
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
