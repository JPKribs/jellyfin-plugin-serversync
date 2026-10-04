using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Configuration;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Finds the mapping a hint travels through. On the sending side a local user and path are matched to
/// the entry's local ids and local roots, so only objects the operator mapped to that peer raise hints.
/// On the receiving side the origin's user id and path are matched to the entry's source ids and
/// source roots, the same mappings the scan uses, so a hint and a scan land on the same local object.
/// </summary>
public static class HintMapping
{
    /// <summary>Finds the enabled user mapping for a local user, on the sending side.</summary>
    /// <param name="peer">The peer entry.</param>
    /// <param name="localUserId">The local user id.</param>
    /// <returns>The mapping, or null.</returns>
    public static UserMapping? FindByLocalUser(SourceServer peer, Guid localUserId)
    {
        ArgumentNullException.ThrowIfNull(peer);
        return peer.GetEnabledUserMappings().FirstOrDefault(m => SameId(m.LocalUserId, localUserId));
    }

    /// <summary>Finds the enabled library mapping whose local root holds a local path, on the sending side.</summary>
    /// <param name="peer">The peer entry.</param>
    /// <param name="localPath">The local path.</param>
    /// <returns>The mapping, or null.</returns>
    public static LibraryMapping? FindByLocalPath(SourceServer peer, string? localPath)
    {
        ArgumentNullException.ThrowIfNull(peer);
        return string.IsNullOrEmpty(localPath)
            ? null
            : peer.GetEnabledLibraryMappings().FirstOrDefault(m => IsUnder(localPath, m.LocalRootPath));
    }

    /// <summary>Finds the enabled user mapping for a user id on the origin, on the receiving side.</summary>
    /// <param name="origin">The origin entry.</param>
    /// <param name="originUserId">The origin's user id.</param>
    /// <returns>The mapping, or null.</returns>
    public static UserMapping? FindBySourceUser(SourceServer origin, Guid originUserId)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return origin.GetEnabledUserMappings().FirstOrDefault(m => SameId(m.SourceUserId, originUserId));
    }

    /// <summary>Finds the enabled library mapping whose source root holds a path on the origin, on the receiving side.</summary>
    /// <param name="origin">The origin entry.</param>
    /// <param name="originPath">The path on the origin.</param>
    /// <returns>The mapping, or null.</returns>
    public static LibraryMapping? FindBySourcePath(SourceServer origin, string? originPath)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return string.IsNullOrEmpty(originPath)
            ? null
            : origin.GetEnabledLibraryMappings().FirstOrDefault(m => IsUnder(originPath, m.SourceRootPath));
    }

    /// <summary>Whether a path lies under a root, with either separator and in any case.</summary>
    /// <param name="path">The path.</param>
    /// <param name="root">The root.</param>
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

    private static string Normalize(string path) => path.Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/');

    private static bool SameId(string? stored, Guid id) => Guid.TryParse(stored, out var parsed) && parsed == id;
}
