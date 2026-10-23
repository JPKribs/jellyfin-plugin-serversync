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
    /// The longest name a recycled file may have, in UTF-8 bytes. Most filesystems cap a name at 255
    /// bytes, and room is left for the "_" and eight hex digits added when the name is taken.
    /// </summary>
    public const int MaxRecycledNameBytes = 255 - 9;

    /// <summary>
    /// Builds a recycled file name in the form <c>path.to.file_2026-01-29_17-30-45.ext</c>. A deep path
    /// would make a name longer than the filesystem allows, so the folder part is then cut to its end,
    /// behind a short hash of the whole folder path that keeps two cut names apart.
    /// </summary>
    public static string GenerateRecycledFileName(string originalPath)
        => GenerateRecycledFileName(originalPath, DateTime.UtcNow);

    /// <summary>Builds a recycled file name for a given time. See <see cref="GenerateRecycledFileName(string)"/>.</summary>
    /// <param name="originalPath">The file's path.</param>
    /// <param name="utcNow">The time of recycling.</param>
    /// <returns>The name.</returns>
    public static string GenerateRecycledFileName(string originalPath, DateTime utcNow)
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
        var timestamp = utcNow.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var tail = $"{nameWithoutExt}_{timestamp}{extension}";

        if (string.IsNullOrEmpty(encodedPath))
        {
            return FitName(string.Empty, nameWithoutExt, timestamp, extension);
        }

        var full = $"{encodedPath}.{tail}";
        if (Utf8Length(full) <= MaxRecycledNameBytes)
        {
            return full;
        }

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(directory)))[..8].ToLowerInvariant();
        return FitName(hash + "~" + encodedPath, nameWithoutExt, timestamp, extension, keepPrefixChars: hash.Length + 1);
    }

    // Shortens the folder part from its start, keeping its first keepPrefixChars (the hash), then the
    // file's own name if it alone is too long. The timestamp and extension always survive, since the
    // retention cleanup reads the one and Jellyfin users recognise files by the other.
    private static string FitName(string folderPart, string stem, string timestamp, string extension, int keepPrefixChars = 0)
    {
        var suffix = $"_{timestamp}{extension}";
        var budget = MaxRecycledNameBytes - Utf8Length(suffix);
        if (folderPart.Length > 0)
        {
            var prefix = folderPart[..keepPrefixChars];
            var rest = folderPart[keepPrefixChars..];
            var forRest = budget - Utf8Length(prefix) - Utf8Length(stem) - 1;
            if (forRest > 0)
            {
                return prefix + KeepEnd(rest, forRest) + "." + stem + suffix;
            }

            // The file's own name leaves no room for any folder: keep the hash alone.
            var stemRoom = budget - Utf8Length(prefix) - 1;
            return prefix + "." + KeepStart(stem, stemRoom) + suffix;
        }

        return KeepStart(stem, budget) + suffix;
    }

    private static int Utf8Length(string value) => System.Text.Encoding.UTF8.GetByteCount(value);

    // The longest end of the text that fits in the given bytes, never splitting a character.
    private static string KeepEnd(string text, int maxBytes)
    {
        var start = 0;
        while (start < text.Length && Utf8Length(text[start..]) > maxBytes)
        {
            start += char.IsHighSurrogate(text[start]) ? 2 : 1;
        }

        return text[start..];
    }

    // The longest start of the text that fits in the given bytes, never splitting a character.
    private static string KeepStart(string text, int maxBytes)
    {
        var end = text.Length;
        while (end > 0 && Utf8Length(text[..end]) > maxBytes)
        {
            end -= end >= 2 && char.IsLowSurrogate(text[end - 1]) ? 2 : 1;
        }

        return text[..end];
    }

    // A recycled file as the bin names it: "<name>_yyyy-MM-dd_HH-mm-ss<ext>", with "_" and eight hex
    // digits before the extension when that name was taken.
    private static readonly System.Text.RegularExpressions.Regex RecycledFileName = new(
        "^.+_(?<at>[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{2}-[0-9]{2}-[0-9]{2})(_[0-9a-f]{8})?(\\.[^.]*)?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads the UTC time a file was recycled at from its name. Only names the recycling bin builds
    /// qualify, so a file someone else put in the folder is never read as one of the plugin's.
    /// </summary>
    /// <param name="filePath">The file's path or name.</param>
    /// <param name="recycledAt">The time it was recycled at.</param>
    /// <returns>True when the name is one the recycling bin builds.</returns>
    public static bool TryReadRecycledTime(string filePath, out DateTime recycledAt)
    {
        recycledAt = default;
        var match = RecycledFileName.Match(Path.GetFileName(filePath ?? string.Empty));
        return match.Success && DateTime.TryParseExact(
            match.Groups["at"].Value,
            "yyyy-MM-dd_HH-mm-ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out recycledAt);
    }

    /// <summary>
    /// Extracts the UTC timestamp from a recycled file name, or null if the pattern doesn't match.
    /// </summary>
    public static DateTime? ExtractTimestampFromFileName(string filePath)
        => TryReadRecycledTime(filePath, out var at) ? at : null;

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
