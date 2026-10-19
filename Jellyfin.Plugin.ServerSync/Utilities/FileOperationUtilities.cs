using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// File-system operations: atomic moves, companion-file discovery, recycled-name
/// generation, and directory write-permission probes.
/// </summary>
public static class FileOperationUtilities
{
    /// <summary>
    /// Subtitle / metadata / image extensions discovered as companion files for a media file.
    /// </summary>
    public static readonly string[] CompanionExtensions = { ".srt", ".sub", ".ass", ".ssa", ".vtt", ".nfo", ".jpg", ".png" };

    private static readonly string[] ItemImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".tbn" };

    // Images Jellyfin saves next to one media file, named after it: "<name>-thumb.jpg" and the like.
    private static readonly System.Text.RegularExpressions.Regex ItemImageSuffix = new(
        "^-(thumb|poster|fanart|landscape|banner|logo|clearlogo|clearart|disc|backdrop|background|art)[0-9]*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// What Jellyfin generates for one media file and names after it: its local images
    /// ("name-thumb.jpg") and its trickplay folder ("name.trickplay"). Matching is on the exact name, so
    /// another version in the same folder ("name - 4K.mkv") is never matched.
    /// </summary>
    /// <param name="mainFilePath">Path to the media file.</param>
    /// <returns>The image files, and the trickplay folder when there is one.</returns>
    public static (List<string> Images, string? TrickplayFolder) GetItemArtifacts(string mainFilePath)
    {
        var images = new List<string>();
        var directory = Path.GetDirectoryName(mainFilePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return (images, null);
        }

        var stem = Path.GetFileNameWithoutExtension(mainFilePath);
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, stem + "-*"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length > stem.Length
                    && name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                    && Array.Exists(ItemImageExtensions, e => string.Equals(e, Path.GetExtension(file), StringComparison.OrdinalIgnoreCase))
                    && ItemImageSuffix.IsMatch(name[stem.Length..]))
                {
                    images.Add(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable folder: nothing is matched, so nothing extra is removed.
        }

        var trickplay = Path.Combine(directory, stem + ".trickplay");
        return (images, Directory.Exists(trickplay) ? trickplay : null);
    }

    /// <summary>
    /// Moves a file with overwrite semantics, retrying on transient IO failures. Retry waits use
    /// <see cref="Task.Delay(int, CancellationToken)"/> so they don't block thread-pool threads.
    /// </summary>
    public static async Task MoveFileWithOverwriteAsync(
        string sourcePath,
        string destinationPath,
        ILogger? logger = null,
        int maxRetries = 3,
        int retryDelayMs = 100,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (IOException ex) when (attempt < maxRetries)
            {
                logger?.LogWarning(
                    ex,
                    "File move attempt {Attempt}/{MaxRetries} failed for {Destination}, retrying",
                    attempt,
                    maxRetries,
                    Path.GetFileName(destinationPath));
                await Task.Delay(retryDelayMs * attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        File.Move(sourcePath, destinationPath, overwrite: true);
    }

    /// <summary>
    /// Builds a recycled file name in the form <c>path.to.file_2026-01-29_17-30-45.ext</c>.
    /// </summary>
    public static string GenerateRecycledFileName(string originalPath)
    {
        var fileName = Path.GetFileName(originalPath);
        var directory = Path.GetDirectoryName(originalPath) ?? string.Empty;

        var encodedPath = directory
            .Replace(Path.DirectorySeparatorChar, '.')
            .Replace(Path.AltDirectorySeparatorChar, '.')
            .Replace(':', '.')
            .Trim('.');

        // InvariantCulture: ExtractTimestampFromFileName parses with it, and a
        // host on a non-Gregorian calendar (th-TH, ar-SA) would otherwise write
        // a year the parser can't read, silently dropping retention back to
        // LastWriteTimeUtc.
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        if (!string.IsNullOrEmpty(encodedPath))
        {
            return $"{encodedPath}.{nameWithoutExt}_{timestamp}{extension}";
        }

        return $"{nameWithoutExt}_{timestamp}{extension}";
    }

    /// <summary>
    /// Extracts the UTC timestamp from a recycled file name, or null if the pattern doesn't match.
    /// </summary>
    public static DateTime? ExtractTimestampFromFileName(string filePath)
    {
        try
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);

            // Pattern: _YYYY-MM-DD_HH-mm-ss at the end.
            var lastUnderscore = fileName.LastIndexOf('_');
            if (lastUnderscore < 0)
            {
                return null;
            }

            var secondLastUnderscore = fileName.LastIndexOf('_', lastUnderscore - 1);
            if (secondLastUnderscore < 0)
            {
                return null;
            }

            var timestampPart = fileName[(secondLastUnderscore + 1)..];

            if (DateTime.TryParseExact(
                timestampPart,
                "yyyy-MM-dd_HH-mm-ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var timestamp))
            {
                return timestamp;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // Filename doesn't match the expected timestamp pattern.
        }

        return null;
    }

    /// <summary>
    /// Returns companion files (subtitles, NFOs, images) that share the main file's stem.
    /// Match is exact on <c>{base}.</c> so siblings like <c>Foo 2.mkv</c> don't bleed into <c>Foo.mkv</c>'s companions.
    /// </summary>
    public static List<string> GetCompanionFiles(string mainFilePath)
    {
        var companions = new List<string>();

        var directory = Path.GetDirectoryName(mainFilePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return companions;
        }

        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(mainFilePath);
        var prefix = fileNameWithoutExt + ".";

        foreach (var ext in CompanionExtensions)
        {
            try
            {
                foreach (var candidate in Directory.EnumerateFiles(directory, fileNameWithoutExt + "*" + ext))
                {
                    var name = Path.GetFileName(candidate);
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    {
                        companions.Add(candidate);
                    }
                }
            }
            catch (IOException)
            {
                // Skip this extension on directory-access failure.
            }
        }

        return companions;
    }

    /// <summary>
    /// Probes a directory for write permission by creating and deleting a sentinel file.
    /// Creates the directory if it doesn't exist.
    /// </summary>
    public static bool HasWritePermission(string directoryPath)
    {
        if (string.IsNullOrEmpty(directoryPath))
        {
            return false;
        }

        try
        {
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
                return true;
            }

            var testFile = Path.Combine(directoryPath, $".write_test_{Guid.NewGuid():N}");
            try
            {
                using (File.Create(testFile, 1, FileOptions.DeleteOnClose))
                {
                    return true;
                }
            }
            finally
            {
                if (File.Exists(testFile))
                {
                    try
                    {
                        File.Delete(testFile);
                    }
                    catch (IOException)
                    {
                        // DeleteOnClose handles cleanup. Explicit delete is best-effort.
                    }
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
