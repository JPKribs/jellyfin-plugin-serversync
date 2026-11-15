using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;

namespace Jellyfin.Plugin.ServerSync.Services.ContentSync.Downloads;

/// <summary>
/// Checks paths and files before a download or a deletion acts on them.
/// </summary>
public static class FileValidationService
{
    /// <summary>
    /// Checks whether a local file's size is close enough to the recorded source size to be the same file.
    /// An unknown source size proves nothing about the local file, so it never counts as a match.
    /// </summary>
    /// <param name="localSize">The local file's size in bytes.</param>
    /// <param name="sourceSize">The size the source reported in bytes, which Jellyfin metadata may state loosely.</param>
    /// <param name="toleranceBytes">How many bytes the sizes may differ by, where 0 requires them to be equal.</param>
    /// <returns><c>true</c> when the sizes are equal or within the tolerance.</returns>
    public static bool IsSizeWithinDriftTolerance(long localSize, long sourceSize, long toleranceBytes)
    {
        if (sourceSize <= 0)
        {
            return false;
        }

        return Math.Abs(localSize - sourceSize) <= Math.Max(0, toleranceBytes);
    }

    /// <summary>
    /// Checks that a download's path is set, lies inside a root folder, and is not used by another item.
    /// </summary>
    /// <param name="localPath">The path the download would write to.</param>
    /// <param name="sourceItemId">The item the path belongs to, which is left out of the collision check.</param>
    /// <param name="config">Supplies the root folders the path must lie inside.</param>
    /// <param name="manager">The Content sync table searched for another item on the same path.</param>
    /// <returns>Whether the path is valid, with the reason when it is not.</returns>
    public static PathValidationResult ValidatePath(
        string? localPath,
        string sourceItemId,
        PluginConfiguration config,
        ContentSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(sourceItemId);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(manager);

        if (string.IsNullOrEmpty(localPath))
        {
            return new PathValidationResult(false, "No local path configured");
        }

        var normalizedPath = Path.GetFullPath(localPath);
        if (!IsPathWithinLibrary(normalizedPath, config))
        {
            return new PathValidationResult(false, "Path traversal detected - path escapes library boundaries");
        }

        if (manager.CheckPathCollision(localPath, sourceItemId))
        {
            return new PathValidationResult(false, "Path collision - another item is already using this path");
        }

        return new PathValidationResult(true, null);
    }

    /// <summary>
    /// Finds the root folder of the enabled library mapping that contains a path.
    /// The check compares whole path segments, so "/media/videos_evil" never matches the root folder "/media/videos".
    /// </summary>
    /// <param name="path">The path to place.</param>
    /// <param name="config">Supplies the root folder of every enabled library mapping.</param>
    /// <returns>The root folder, or null when no mapping contains the path.</returns>
    public static string? GetContainingLibraryRoot(string? path, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var roots = config.GetEnabledLibraryMappings().Where(m => !string.IsNullOrEmpty(m.LocalRootPath)).Select(m => m.LocalRootPath);
        return PathContainment.FirstRootUnder(path, roots, deepest: false);
    }

    /// <summary>
    /// Checks whether any folder between a file and its root folder is a symlink or junction, which could make a deletion inside the root folder remove a physical file elsewhere.
    /// The file itself may be a symlink, and a check that fails counts as symlinked.
    /// </summary>
    /// <param name="filePath">The file whose parent folders are checked.</param>
    /// <param name="libraryRootPath">The root folder the file sits under, where the check stops.</param>
    /// <returns><c>true</c> when a folder between them is a symlink, or the check failed.</returns>
    public static bool HasSymlinkedDirectoryComponent(string filePath, string libraryRootPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));

            while (!string.IsNullOrEmpty(dir))
            {
                if (!PathContainment.IsStrictlyUnder(dir, libraryRootPath))
                {
                    break;
                }

                var di = new DirectoryInfo(dir);
                if (di.Exists && (di.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                dir = Path.GetDirectoryName(dir);
            }

            return false;
        }

        // Any failure counts as symlinked, since refusing is the safe side for a deletion.
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// Checks that a path lies inside the root folder of an enabled library mapping.
    /// </summary>
    /// <param name="path">The path to place.</param>
    /// <param name="config">Supplies the root folder of every enabled library mapping.</param>
    /// <returns><c>true</c> when a root folder contains the path.</returns>
    public static bool IsPathWithinLibrary(string? path, PluginConfiguration config)
        => GetContainingLibraryRoot(path, config) != null;

    /// <summary>
    /// Checks whether a download can be skipped because the local file exists with a matching size.
    /// </summary>
    /// <param name="item">The row whose local path and source size are compared.</param>
    /// <param name="sizeMatchToleranceBytes">How many bytes the sizes may differ by, where 0 requires them to be equal.</param>
    /// <param name="skipReason">Why the download is skipped, or null when it is not.</param>
    /// <returns><c>true</c> when the download should be skipped.</returns>
    public static bool ShouldSkipDownload(SyncItem item, long sizeMatchToleranceBytes, out string? skipReason)
    {
        ArgumentNullException.ThrowIfNull(item);

        skipReason = null;

        if (string.IsNullOrEmpty(item.LocalPath))
        {
            return false;
        }

        if (!File.Exists(item.LocalPath))
        {
            return false;
        }

        try
        {
            var localInfo = new FileInfo(item.LocalPath);

            if (IsSizeWithinDriftTolerance(localInfo.Length, item.SourceSize, sizeMatchToleranceBytes))
            {
                skipReason = $"Local file already exists with matching size ({FormatUtilities.FormatBytes(localInfo.Length)})";
                return true;
            }

            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks that this server can write to a file's folder, creating the folder when it is missing.
    /// </summary>
    /// <param name="filePath">Where the file will be written.</param>
    /// <returns><c>true</c> when the folder is writable.</returns>
    public static bool HasWritePermission(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        return FileOperationUtilities.HasWritePermission(directory);
    }
}
