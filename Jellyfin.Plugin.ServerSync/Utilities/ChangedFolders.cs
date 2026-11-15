using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Turns the files a content run wrote or removed into the folders Jellyfin is told to look at again.
/// </summary>
public static class ChangedFolders
{
    /// <summary>
    /// The most folders reported one by one. Past this a single full library scan is cheaper.
    /// </summary>
    public const int MaxTargeted = 250;

    /// <summary>
    /// Lists the distinct folders holding the changed files, dropping any folder inside another in the list.
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

    /// <summary>
    /// Whether Jellyfin knows a folder at or above each changed folder, so reporting the folder reaches a library.
    /// Jellyfin does not record an empty library's folder, so a first run into one needs a full scan instead.
    /// </summary>
    /// <param name="folders">The changed folders.</param>
    /// <param name="known">Whether Jellyfin holds a library folder at a path.</param>
    /// <returns><c>true</c> when every folder has a known folder at or above it.</returns>
    public static bool AllReachable(IEnumerable<string> folders, Func<string, bool> known)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(known);
        foreach (var folder in folders)
        {
            var path = folder;
            var found = false;
            while (!string.IsNullOrEmpty(path))
            {
                if (known(path))
                {
                    found = true;
                    break;
                }

                var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
                path = string.Equals(parent, path, StringComparison.Ordinal) ? null : parent;
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a folder lies inside another on a separator boundary.</summary>
    private static bool IsInside(string folder, string outer)
        => folder.Length > outer.Length
            && folder.StartsWith(outer, StringComparison.Ordinal)
            && (folder[outer.Length] == Path.DirectorySeparatorChar || folder[outer.Length] == Path.AltDirectorySeparatorChar);
}
