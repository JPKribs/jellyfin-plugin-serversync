using System;
using System.IO;
using System.Linq;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Puts back the files a replacing download set aside as <c>name.replacing.&lt;id&gt;</c> when the server stopped before the new file moved in.
/// Every move goes into a free name, so nothing is overwritten or deleted.
/// </summary>
public static class ReplacementRecovery
{
    /// <summary>The text between a set-aside file's own name and its id.</summary>
    private const string Marker = ".replacing.";

    /// <summary>
    /// Restores the file at <paramref name="localPath"/> from its set-aside copy when it is missing, with every set-aside companion whose name is free.
    /// </summary>
    /// <param name="localPath">Where the file belongs.</param>
    /// <param name="logger">Writes a log entry for each file restored or refused.</param>
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

            // Newest first, so the restored file is the one that was here last.
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
                    logger.LogWarning("Restored {Path}, which an interrupted replacement had set aside", FormatUtilities.SanitizeForLog(target));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Could not restore {Path} from {Aside}", FormatUtilities.SanitizeForLog(target), FormatUtilities.SanitizeForLog(aside));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not look for set-aside files beside {Path}", FormatUtilities.SanitizeForLog(localPath));
        }

        return restored;
    }

    /// <summary>
    /// Reads the name a set-aside file had, which is what precedes the marker when 32 hex digits follow it.
    /// </summary>
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
