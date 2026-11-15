using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Configuration;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Finds the mapping a hint travels through, by local ids and roots when sending and by source ids and roots when receiving.
/// These are the mappings the scan uses, so a hint and a scan land on the same local object.
/// </summary>
public static class HintMapping
{
    /// <summary>Finds the enabled user mapping for a local user, on the sending side.</summary>
    /// <param name="peer">The server entry whose user mappings are searched.</param>
    /// <param name="localUserId">The local user to find a mapping for.</param>
    /// <returns>The mapping, or null.</returns>
    public static UserMapping? FindByLocalUser(SourceServer peer, Guid localUserId)
    {
        ArgumentNullException.ThrowIfNull(peer);
        return peer.GetEnabledUserMappings().FirstOrDefault(m => SameId(m.LocalUserId, localUserId));
    }

    /// <summary>Finds the enabled library mapping whose local root holds a local path, on the sending side.</summary>
    /// <param name="peer">The server entry whose library mappings are searched.</param>
    /// <param name="localPath">The local path a root must hold.</param>
    /// <returns>The mapping, or null.</returns>
    public static LibraryMapping? FindByLocalPath(SourceServer peer, string? localPath)
    {
        ArgumentNullException.ThrowIfNull(peer);
        return string.IsNullOrEmpty(localPath)
            ? null
            : MostSpecific(peer.GetEnabledLibraryMappings().Where(m => IsUnder(localPath, m.LocalRootPath)), m => m.LocalRootPath);
    }

    /// <summary>Finds the enabled user mapping for a user id on the origin, on the receiving side.</summary>
    /// <param name="origin">The server entry of the origin, whose user mappings are searched.</param>
    /// <param name="originUserId">The origin's user id.</param>
    /// <returns>The mapping, or null.</returns>
    public static UserMapping? FindBySourceUser(SourceServer origin, Guid originUserId)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return origin.GetEnabledUserMappings().FirstOrDefault(m => SameId(m.SourceUserId, originUserId));
    }

    /// <summary>Finds the enabled library mapping whose source root holds a path on the origin, on the receiving side.</summary>
    /// <param name="origin">The server entry of the origin, whose library mappings are searched.</param>
    /// <param name="originPath">The path on the origin.</param>
    /// <returns>The mapping, or null.</returns>
    public static LibraryMapping? FindBySourcePath(SourceServer origin, string? originPath)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return string.IsNullOrEmpty(originPath)
            ? null
            : MostSpecific(origin.GetEnabledLibraryMappings().Where(m => IsUnder(originPath, m.SourceRootPath)), m => m.SourceRootPath);
    }

    /// <summary>Whether a path lies under a root, with either separator and in any case.</summary>
    /// <param name="path">The path to test.</param>
    /// <param name="root">The root folder it may lie under.</param>
    /// <returns><c>true</c> when the path is the root or inside it.</returns>
    public static bool IsUnder(string path, string? root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
        {
            return false;
        }

        var normalPath = Normalize(path);
        var normalRoot = Normalize(root).TrimEnd('/');
        if (normalRoot.Length == 0)
        {
            return false;
        }

        return normalPath.Equals(normalRoot, StringComparison.OrdinalIgnoreCase)
            || normalPath.StartsWith(normalRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Picks the mapping with the deepest root, since mappings can overlap and the deepest is the one meant.</summary>
    private static LibraryMapping? MostSpecific(IEnumerable<LibraryMapping> matches, Func<LibraryMapping, string?> rootOf)
        => matches.OrderByDescending(m => Normalize(rootOf(m) ?? string.Empty).TrimEnd('/').Length).FirstOrDefault();

    /// <summary>Writes a path with forward slashes so both separators compare equal.</summary>
    private static string Normalize(string path) => path.Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/');

    /// <summary>Whether a stored id parses to the given id.</summary>
    private static bool SameId(string? stored, Guid id) => Guid.TryParse(stored, out var parsed) && parsed == id;
}
