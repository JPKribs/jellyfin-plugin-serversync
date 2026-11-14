using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Puts back what an interrupted replacement moved aside. A download that replaces a file first moves
/// the file and the files that go with it to <c>name.replacing.&lt;id&gt;</c>, then moves the new file
/// in. A server stopped between the two leaves the local file under a name nothing plays. Every move
/// here goes into a name that is free, so nothing is overwritten and nothing is deleted.
/// </summary>
public static class ReplacementRecovery
{
    private const string Marker = ".replacing.";

    /// <summary>
    /// Restores the file at <paramref name="localPath"/> when it is missing and a set-aside copy of it
    /// is there, and every set-aside file that goes with it whose own name is free.
    /// </summary>
    /// <param name="localPath">Where the file belongs.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>How many files were put back.</returns>
    public static int Restore(string? localPath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (string.IsNullOrEmpty(localPath) || Path.GetDirectoryName(localPath) is not { Length: > 0 } folder || !Directory.Exists(folder))
        {
            return 0;
        }

        var restored = 0;
        try
        {
            var name = Path.GetFileName(localPath);
            var stem = Path.GetFileNameWithoutExtension(localPath);

            // Newest first, so the file is the one that was here last.
            foreach (var aside in Directory.EnumerateFiles(folder)
                .Where(path => OriginalNameOf(path) is not null)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList())
            {
                var original = OriginalNameOf(aside)!;
                var isMain = string.Equals(original, name, StringComparison.OrdinalIgnoreCase);
                var goesWithIt = !isMain
                    && original.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                    && original.Length > stem.Length
                    && (original[stem.Length] == '.' || original[stem.Length] == '-');
                var target = Path.Combine(folder, original);
                if ((!isMain && !goesWithIt) || File.Exists(target) || Directory.Exists(target))
                {
                    continue;
                }

                try
                {
                    File.Move(aside, target);
                    restored++;
                    logger.LogWarning("Restored {Path}, which an interrupted replacement had set aside", target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Could not restore {Path} from {Aside}", target, aside);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not look for set-aside files beside {Path}", localPath);
        }

        return restored;
    }

    // The name a set-aside file had: what comes before ".replacing." when 32 hex digits follow it.
    private static string? OriginalNameOf(string path)
    {
        var file = Path.GetFileName(path);
        var at = file.LastIndexOf(Marker, StringComparison.Ordinal);
        if (at <= 0)
        {
            return null;
        }

        var id = file[(at + Marker.Length)..];
        return id.Length == 32 && id.All(Uri.IsHexDigit) ? file[..at] : null;
    }
}
