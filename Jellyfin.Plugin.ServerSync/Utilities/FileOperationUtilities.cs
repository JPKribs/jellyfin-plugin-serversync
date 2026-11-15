using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Moves files, finds companion files, names recycled files, and probes folders for write permission.
/// </summary>
public static class FileOperationUtilities
{
    /// <summary>
    /// The subtitle, metadata, and image extensions that mark a companion of a media file.
    /// </summary>
    public static readonly string[] CompanionExtensions = { ".srt", ".sub", ".ass", ".ssa", ".vtt", ".nfo", ".jpg", ".png" };

    /// <summary>The extensions of images Jellyfin saves beside a media file.</summary>
    private static readonly string[] ItemImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".tbn" };

    /// <summary>
    /// Images Jellyfin saves next to one media file, named after it: "&lt;name&gt;-thumb.jpg" and the like.
    /// </summary>
    private static readonly Regex ItemImageSuffix = new(
        "^-(thumb|poster|fanart|landscape|banner|logo|clearlogo|clearart|disc|backdrop|background|art)[0-9]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Finds the images and trickplay folder Jellyfin named after one media file. The match is on the exact name, so another version in the folder is never matched.
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
            // Another media file of the same name owns these, so none is matched.
            if (HasSameNamedMedia(directory, stem, mainFilePath))
            {
                return (images, null);
            }

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
            // An unreadable folder matches nothing, so nothing extra is removed.
        }

        var trickplay = Path.Combine(directory, stem + ".trickplay");
        return (images, Directory.Exists(trickplay) ? trickplay : null);
    }

    /// <summary>
    /// Moves a file over any file already there, retrying when an IO failure may pass.
    /// </summary>
    /// <param name="sourcePath">The file to move.</param>
    /// <param name="destinationPath">Where the file goes.</param>
    /// <param name="logger">Writes log entries, or null.</param>
    /// <param name="maxRetries">How many attempts to make.</param>
    /// <param name="retryDelayMs">The wait before the first retry, in milliseconds. Each retry waits longer.</param>
    /// <param name="cancellationToken">Cancels the wait between attempts.</param>
    /// <returns>A task that ends when the file has moved.</returns>
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
    /// The longest name a recycled file may have, in UTF-8 bytes. It leaves room under the 255 byte cap for the suffix added when the name is taken.
    /// </summary>
    public const int MaxRecycledNameBytes = 255 - 9;

    /// <summary>
    /// Builds a recycled file name in the form <c>path.to.file_2026-01-29_17-30-45.ext</c>. A name too long for the filesystem keeps the end of its folder part behind a short hash of the whole folder path.
    /// </summary>
    /// <param name="originalPath">The file's path before it was recycled.</param>
    /// <returns>The recycled file name.</returns>
    public static string GenerateRecycledFileName(string originalPath)
        => GenerateRecycledFileName(originalPath, DateTime.UtcNow);

    /// <summary>Builds a recycled file name for a given time. See <see cref="GenerateRecycledFileName(string)"/>.</summary>
    /// <param name="originalPath">The file's path before it was recycled.</param>
    /// <param name="utcNow">The time of recycling.</param>
    /// <returns>The recycled file name.</returns>
    public static string GenerateRecycledFileName(string originalPath, DateTime utcNow)
    {
        var fileName = Path.GetFileName(originalPath);
        var directory = Path.GetDirectoryName(originalPath) ?? string.Empty;

        var encodedPath = directory
            .Replace(Path.DirectorySeparatorChar, '.')
            .Replace(Path.AltDirectorySeparatorChar, '.')
            .Replace(':', '.')
            .Trim('.');

        var timestamp = utcNow.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
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

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)))[..8].ToLowerInvariant();
        return FitName(hash + "~" + encodedPath, nameWithoutExt, timestamp, extension, keepPrefixChars: hash.Length + 1);
    }

    /// <summary>
    /// Shortens the folder part from its start, keeping the hash, then the file's own name. The time and extension always survive, since the retention cleanup reads the time.
    /// </summary>
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

            // The file name alone fills the budget, so only the hash precedes it.
            var stemRoom = budget - Utf8Length(prefix) - 1;
            return prefix + "." + KeepStart(stem, stemRoom) + suffix;
        }

        return KeepStart(stem, budget) + suffix;
    }

    /// <summary>Counts the UTF-8 bytes of a text.</summary>
    private static int Utf8Length(string value) => Encoding.UTF8.GetByteCount(value);

    /// <summary>
    /// The longest end of the text that fits in the given bytes, never splitting a character.
    /// </summary>
    private static string KeepEnd(string text, int maxBytes)
    {
        var start = 0;
        while (start < text.Length && Utf8Length(text[start..]) > maxBytes)
        {
            start += char.IsHighSurrogate(text[start]) ? 2 : 1;
        }

        return text[start..];
    }

    /// <summary>
    /// The longest start of the text that fits in the given bytes, never splitting a character.
    /// </summary>
    private static string KeepStart(string text, int maxBytes)
    {
        var end = text.Length;
        while (end > 0 && Utf8Length(text[..end]) > maxBytes)
        {
            end -= end >= 2 && char.IsLowSurrogate(text[end - 1]) ? 2 : 1;
        }

        return text[..end];
    }

    /// <summary>
    /// Matches a recycled file as the Recycling Bin names it, with an optional suffix of eight hex digits.
    /// </summary>
    private static readonly Regex RecycledFileName = new(
        "^.+_(?<at>[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{2}-[0-9]{2}-[0-9]{2})(_[0-9a-f]{8})?(\\.[^.]*)?$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads the UTC time a file was recycled at from its name. Only names the Recycling Bin builds qualify, so a stranger's file is never read as one.
    /// </summary>
    /// <param name="filePath">The file's path or name.</param>
    /// <param name="recycledAt">The time it was recycled at.</param>
    /// <returns><c>true</c> when the name is one the Recycling Bin builds.</returns>
    public static bool TryReadRecycledTime(string filePath, out DateTime recycledAt)
    {
        recycledAt = default;
        var match = RecycledFileName.Match(Path.GetFileName(filePath ?? string.Empty));
        return match.Success && DateTime.TryParseExact(
            match.Groups["at"].Value,
            "yyyy-MM-dd_HH-mm-ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out recycledAt);
    }

    /// <summary>
    /// Reads the UTC time from a recycled file name.
    /// </summary>
    /// <param name="filePath">The recycled file's path.</param>
    /// <returns>The time the file was recycled, or null when the name is not a recycled one.</returns>
    public static DateTime? ExtractTimestampFromFileName(string filePath)
        => TryReadRecycledTime(filePath, out var at) ? at : null;

    /// <summary>
    /// Finds the companion files that share the media file's name. A file named after a longer title in the folder belongs to that title.
    /// </summary>
    /// <param name="mainFilePath">The media file's path.</param>
    /// <returns>The companion file paths.</returns>
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

        try
        {
            var sharing = Directory.EnumerateFiles(directory, fileNameWithoutExt + ".*")
                .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(path, mainFilePath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var otherTitles = sharing
                .Where(path => !IsCompanionExtension(path) && !Path.GetFileName(path).Contains(".replacing.", StringComparison.Ordinal))
                .Select(path => Path.GetFileNameWithoutExtension(path) + ".")
                .ToList();

            foreach (var candidate in sharing.Where(IsCompanionExtension))
            {
                var name = Path.GetFileName(candidate);
                if (!otherTitles.Exists(other => name.StartsWith(other, StringComparison.OrdinalIgnoreCase)))
                {
                    companions.Add(candidate);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder matches nothing, so nothing extra is moved or removed.
        }

        return companions;
    }

    /// <summary>
    /// Whether the folder holds another media file with the same name, such as a second container of the title.
    /// </summary>
    private static bool HasSameNamedMedia(string directory, string stem, string mainFilePath)
        => Directory.EnumerateFiles(directory, stem + ".*")
            .Any(path => !string.Equals(path, mainFilePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileNameWithoutExtension(path), stem, StringComparison.OrdinalIgnoreCase)
                && !IsCompanionExtension(path)
                && !Path.GetFileName(path).Contains(".replacing.", StringComparison.Ordinal));

    /// <summary>Whether the path ends in a companion extension.</summary>
    private static bool IsCompanionExtension(string path)
        => Array.Exists(CompanionExtensions, ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Probes a folder for write permission by creating and deleting a file, creating the folder when it is missing.
    /// </summary>
    /// <param name="directoryPath">The folder to probe.</param>
    /// <returns><c>true</c> when files can be written there.</returns>
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
                        // DeleteOnClose already removes the file.
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
