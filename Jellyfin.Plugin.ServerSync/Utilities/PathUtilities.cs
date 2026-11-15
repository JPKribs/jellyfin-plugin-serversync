using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.ServerSync.Models.Configuration;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Translates source paths to local ones and matches paths against a Library Filter.
/// </summary>
public static class PathUtilities
{
    /// <summary>The separators of both Windows and Unix paths.</summary>
    private static readonly char[] PathSeparators = { '/', '\\' };

    /// <summary>
    /// Maps a path on the source to the matching path under the local root folder, dropping any traversal segments.
    /// A path outside <paramref name="sourceRoot"/> maps to <paramref name="localRoot"/> plus the file name.
    /// </summary>
    /// <param name="sourcePath">The file's path on the source.</param>
    /// <param name="sourceRoot">The library's root folder on the source.</param>
    /// <param name="localRoot">The library's root folder on this server.</param>
    /// <returns>The path under the local root.</returns>
    public static string TranslatePath(string sourcePath, string sourceRoot, string localRoot)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            return localRoot;
        }

        ArgumentNullException.ThrowIfNull(sourceRoot);
        ArgumentNullException.ThrowIfNull(localRoot);

        sourceRoot = sourceRoot.TrimEnd(PathSeparators);
        localRoot = localRoot.TrimEnd(PathSeparators);

        if (StartsWithRootSegment(sourcePath, sourceRoot))
        {
            var relativePath = sourcePath.Substring(sourceRoot.Length).TrimStart(PathSeparators);

            var pathParts = relativePath.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (pathParts.Length > 0)
            {
                var result = localRoot;
                foreach (var part in pathParts)
                {
                    // A source path is untrusted, so traversal segments are dropped.
                    if (part == ".." || part == ".")
                    {
                        continue;
                    }

                    result = Path.Combine(result, part);
                }

                var normalizedResult = Path.GetFullPath(result);
                var normalizedRoot = Path.GetFullPath(localRoot + Path.DirectorySeparatorChar);

                // A result that still escapes the root folder falls back to the file name.
                if (!normalizedResult.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(normalizedResult, Path.GetFullPath(localRoot), StringComparison.OrdinalIgnoreCase))
                {
                    return CombineWithLastPart(localRoot, sourcePath);
                }

                return result;
            }

            return localRoot;
        }

        return CombineWithLastPart(localRoot, sourcePath);
    }

    /// <summary>
    /// Joins the local root folder and the last part of a source path. A last part that names no file leaves the root itself.
    /// </summary>
    private static string CombineWithLastPart(string localRoot, string sourcePath)
    {
        var parts = sourcePath.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        var last = parts.Length > 0 ? parts[^1] : string.Empty;
        return last.Length == 0 || last is "." or ".." ? localRoot : Path.Combine(localRoot, last);
    }

    /// <summary>
    /// Whether <paramref name="path"/> lies under <paramref name="root"/> on a segment boundary, so <c>/media/Movies 4K</c> is not under <c>/media/Movies</c>.
    /// An empty root matches everything.
    /// </summary>
    private static bool StartsWithRootSegment(string path, string root)
    {
        if (root.Length == 0)
        {
            return true;
        }

        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Length == root.Length
            || path[root.Length] == '/'
            || path[root.Length] == '\\';
    }

    /// <summary>
    /// Whether the Library Filter leaves an item out. A Whitelist skips what it does not list, and a Blacklist skips what it lists.
    /// </summary>
    /// <param name="sourcePath">The item's path on the source.</param>
    /// <param name="sourceRootPath">The library's root folder on the source.</param>
    /// <param name="filterMode">The Library Filter mode.</param>
    /// <param name="filteredItems">The paths the Library Filter lists, or null.</param>
    /// <returns><c>true</c> when the item is skipped.</returns>
    public static bool IsItemFiltered(
        string sourcePath,
        string sourceRootPath,
        LibraryFilterMode filterMode,
        List<FilteredItem>? filteredItems)
    {
        if (filterMode == LibraryFilterMode.AllowAll || filteredItems == null || filteredItems.Count == 0)
        {
            return false;
        }

        // An item with no path matches no list, so only a Whitelist skips it.
        if (string.IsNullOrEmpty(sourcePath))
        {
            return filterMode == LibraryFilterMode.Whitelist;
        }

        ArgumentNullException.ThrowIfNull(sourceRootPath);

        var normalizedSourcePath = sourcePath.Replace('\\', '/').TrimEnd('/');
        var normalizedRoot = sourceRootPath.TrimEnd(PathSeparators).Replace('\\', '/');

        bool matchesAny = false;
        foreach (var fi in filteredItems)
        {
            if (string.IsNullOrEmpty(fi.Path))
            {
                continue;
            }

            var normalizedFilterPath = fi.Path.Replace('\\', '/').TrimEnd('/');

            var fullFilterPrefix = normalizedFilterPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                ? normalizedFilterPath
                : normalizedRoot + "/" + normalizedFilterPath.TrimStart('/');

            // The match must end on a segment, so "Foo" never matches "Foobar".
            if (normalizedSourcePath.StartsWith(fullFilterPrefix, StringComparison.OrdinalIgnoreCase)
                && (normalizedSourcePath.Length == fullFilterPrefix.Length
                    || normalizedSourcePath[fullFilterPrefix.Length] == '/'))
            {
                matchesAny = true;
                break;
            }
        }

        return filterMode switch
        {
            LibraryFilterMode.Whitelist => !matchesAny,
            LibraryFilterMode.Blacklist => matchesAny,
            _ => false
        };
    }
}
