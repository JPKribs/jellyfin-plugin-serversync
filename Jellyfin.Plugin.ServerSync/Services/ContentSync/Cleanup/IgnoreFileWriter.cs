using System;
using System.IO;

namespace Jellyfin.Plugin.ServerSync.Services.ContentSync.Cleanup;

/// <summary>
/// Hides a title's folder from Jellyfin with an empty <c>.ignore</c> file, and shows it again.
/// </summary>
public static class IgnoreFileWriter
{
    /// <summary>The name of the file that hides a folder.</summary>
    public const string FileName = ".ignore";

    /// <summary>
    /// Finds the first folder under a root folder that holds a path, which is the folder of that title.
    /// </summary>
    /// <param name="localPath">The path of a file under the root folder.</param>
    /// <param name="localRoot">The library's root folder on this server.</param>
    /// <returns>The title's folder, or null when the path is not inside a folder under the root folder.</returns>
    public static string? TitleFolder(string localPath, string localRoot)
    {
        if (string.IsNullOrEmpty(localPath) || string.IsNullOrEmpty(localRoot))
        {
            return null;
        }

        var root = localRoot.Replace('\\', '/').TrimEnd('/');
        if (root.Length == 0 || (root.Length == 2 && root[1] == ':'))
        {
            return null;
        }

        var path = localPath.Replace('\\', '/');
        if (!path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = path[(root.Length + 1)..];
        var slash = relative.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
        {
            return null;
        }

        return Path.Combine(localRoot.TrimEnd('/', '\\'), relative[..slash]);
    }

    /// <summary>
    /// Removes the folder's <c>.ignore</c> file when it is empty. A file with content was made by a person, so it stays.
    /// </summary>
    /// <param name="folder">The folder to show.</param>
    /// <returns><c>true</c> when a file was removed.</returns>
    public static bool UnhideFolder(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        var ignorePath = Path.Combine(folder, FileName);
        if (!File.Exists(ignorePath) || new FileInfo(ignorePath).Length > 0)
        {
            return false;
        }

        File.Delete(ignorePath);
        return true;
    }

    /// <summary>
    /// Writes an empty <c>.ignore</c> file in the folder. A folder that already has one is left as it is.
    /// </summary>
    /// <param name="folder">The folder to hide.</param>
    /// <returns><c>true</c> when a file was written.</returns>
    public static bool HideFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        if (!Directory.Exists(folder))
        {
            return false;
        }

        var ignorePath = Path.Combine(folder, FileName);
        if (File.Exists(ignorePath))
        {
            return false;
        }

        File.WriteAllText(ignorePath, string.Empty);
        return true;
    }
}
