using System;
using System.IO;

namespace Jellyfin.Plugin.ServerSync.Services.ContentSync.Cleanup;

public static class IgnoreFileWriter
{
    public const string FileName = ".ignore";

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

    public static bool UnhideFolder(string folder)
    {
        var ignorePath = Path.Combine(folder, FileName);
        if (!File.Exists(ignorePath) || new FileInfo(ignorePath).Length > 0)
        {
            return false;
        }

        File.Delete(ignorePath);
        return true;
    }

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
