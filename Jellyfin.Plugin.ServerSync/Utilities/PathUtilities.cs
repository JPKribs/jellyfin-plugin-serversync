using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.ServerSync.Models.Configuration;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Path translation and library-filter matching.
/// </summary>
public static class PathUtilities
{
    private static readonly char[] PathSeparators = { '/', '\\' };

    /// <summary>
    /// Maps a source-server path to the corresponding local path, stripping any
    /// path-traversal segments and falling back to <paramref name="localRoot"/> + filename
    /// when the source path doesn't lie under <paramref name="sourceRoot"/>.
    /// </summary>
    public static string TranslatePath(string sourcePath, string sourceRoot, string localRoot)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            return localRoot;
        }

        sourceRoot = sourceRoot.TrimEnd(PathSeparators);
        localRoot = localRoot.TrimEnd(PathSeparators);

        if (sourcePath.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = sourcePath.Substring(sourceRoot.Length).TrimStart(PathSeparators);

            var pathParts = relativePath.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (pathParts.Length > 0)
            {
                var result = localRoot;
                foreach (var part in pathParts)
                {
                    // Defends against directory traversal from untrusted source paths.
                    if (part == ".." || part == ".")
                    {
                        continue;
                    }

                    result = Path.Combine(result, part);
                }

                // Belt-and-suspenders: even after stripping .. segments, confirm the
                // result is still under localRoot before returning it.
                var normalizedResult = Path.GetFullPath(result);
                var normalizedRoot = Path.GetFullPath(localRoot + Path.DirectorySeparatorChar);
                if (!normalizedResult.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(normalizedResult, Path.GetFullPath(localRoot), StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(localRoot, Path.GetFileName(sourcePath));
                }

                return result;
            }

            return localRoot;
        }

        var fileName = Path.GetFileName(sourcePath);
        return Path.Combine(localRoot, fileName);
    }

    /// <summary>
    /// Returns true if the item should be SKIPPED for the given filter mode + list.
    /// Whitelist skips non-matches, Blacklist skips matches, AllowAll skips nothing.
    /// </summary>
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

        if (string.IsNullOrEmpty(sourcePath))
        {
            // Pathless item: Whitelist can't match (skip), Blacklist can't match (allow).
            return filterMode == LibraryFilterMode.Whitelist;
        }

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

            string fullFilterPrefix;
            if (normalizedFilterPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                fullFilterPrefix = normalizedFilterPath;
            }
            else
            {
                fullFilterPrefix = normalizedRoot + "/" + normalizedFilterPath.TrimStart('/');
            }

            // StartsWith + segment-boundary check: "Foo" must not match "Foobar".
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
