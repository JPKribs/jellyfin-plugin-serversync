using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Turns the files a content run wrote or removed into the folders Jellyfin is told to look at again,
/// so a run that touched a few titles refreshes those titles instead of every library.
/// </summary>
public static class ChangedFolders
{
    /// <summary>
    /// The most folders reported one by one. Past this a single full library scan is cheaper than as
    /// many separate refreshes, as on a first run that copies a whole library.
    /// </summary>
    public const int MaxTargeted = 250;

    /// <summary>
    /// The distinct folders holding the changed files. A folder inside another one in the list is
    /// dropped, since refreshing the outer folder covers it.
    /// </summary>
    /// <param name="changedFiles">The files written or removed.</param>
    /// <returns>The folders, outermost only.</returns>
    public static IReadOnlyList<string> For(IEnumerable<string> changedFiles)
    {
        ArgumentNullException.ThrowIfNull(changedFiles);
        var folders = changedFiles
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(Path.GetDirectoryName)
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => Path.TrimEndingDirectorySeparator(d!))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(d => d.Length)
            .ToList();

        var kept = new List<string>();
        foreach (var folder in folders)
        {
            if (!kept.Any(outer => IsInside(folder, outer)))
            {
                kept.Add(folder);
            }
        }

        return kept;
    }

    private static bool IsInside(string folder, string outer)
        => folder.Length > outer.Length
            && folder.StartsWith(outer, StringComparison.Ordinal)
            && (folder[outer.Length] == Path.DirectorySeparatorChar || folder[outer.Length] == Path.AltDirectorySeparatorChar);
}
