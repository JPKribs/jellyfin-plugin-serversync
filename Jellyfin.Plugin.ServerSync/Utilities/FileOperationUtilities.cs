using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Utility methods for file operations.
/// </summary>
public static class FileOperationUtilities
{
    /// <summary>
    /// Common companion file extensions (subtitles, metadata, images).
    /// </summary>
    public static readonly string[] CompanionExtensions = { ".srt", ".sub", ".ass", ".ssa", ".vtt", ".nfo", ".jpg", ".png" };

    /// <summary>
    /// Moves a file with atomic overwrite semantics where possible.
    /// Includes retry logic for transient failures.
    /// </summary>
    /// <param name="sourcePath">Source file path.</param>
    /// <param name="destinationPath">Destination file path.</param>
    /// <param name="logger">Optional logger for retry warnings.</param>
    /// <param name="maxRetries">Maximum number of retry attempts.</param>
    /// <param name="retryDelayMs">Base delay between retries in milliseconds.</param>
    public static void MoveFileWithOverwrite(
        string sourcePath,
        string destinationPath,
        ILogger? logger = null,
        int maxRetries = 3,
        int retryDelayMs = 100)
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
                Thread.Sleep(retryDelayMs * attempt);
            }
        }

        // Final attempt - let exception propagate
        File.Move(sourcePath, destinationPath, overwrite: true);
    }

    /// <summary>
    /// Async version of MoveFileWithOverwrite that uses Task.Delay instead of Thread.Sleep.
    /// Preferred for callers in async contexts (download pipeline, etc.) to avoid blocking thread pool threads.
    /// </summary>
    /// <param name="sourcePath">Source file path.</param>
    /// <param name="destinationPath">Destination file path.</param>
    /// <param name="logger">Optional logger for retry warnings.</param>
    /// <param name="maxRetries">Maximum number of retry attempts.</param>
    /// <param name="retryDelayMs">Base delay between retries in milliseconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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

        // Final attempt - let exception propagate
        File.Move(sourcePath, destinationPath, overwrite: true);
    }

    /// <summary>
    /// Generates a recycled file name with path encoded and timestamp.
    /// Format: path.to.file_2026-01-29_17-30-45.ext
    /// </summary>
    /// <param name="originalPath">Original file path.</param>
    /// <returns>Recycled file name.</returns>
    public static string GenerateRecycledFileName(string originalPath)
    {
        var fileName = Path.GetFileName(originalPath);
        var directory = Path.GetDirectoryName(originalPath) ?? string.Empty;

        // Encode the directory path: replace path separators with periods
        var encodedPath = directory
            .Replace(Path.DirectorySeparatorChar, '.')
            .Replace(Path.AltDirectorySeparatorChar, '.')
            .Replace(':', '.')
            .Trim('.');

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        if (!string.IsNullOrEmpty(encodedPath))
        {
            return $"{encodedPath}.{nameWithoutExt}_{timestamp}{extension}";
        }

        return $"{nameWithoutExt}_{timestamp}{extension}";
    }

    /// <summary>
    /// Attempts to extract the UTC timestamp from a recycled file name.
    /// </summary>
    /// <param name="filePath">Path to recycled file.</param>
    /// <returns>Extracted timestamp or null if not parseable.</returns>
    public static DateTime? ExtractTimestampFromFileName(string filePath)
    {
        try
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);

            // Look for timestamp pattern: _YYYY-MM-DD_HH-mm-ss at the end
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
            // Filename doesn't contain the expected timestamp pattern
        }

        return null;
    }

    /// <summary>
    /// Gets companion files for a main file (subtitles, metadata, images).
    /// Matches Jellyfin's external-file naming convention: companions live in
    /// the same directory and have a name of the form
    /// <c>{base}.{ext}</c> or <c>{base}.{language}.{ext}</c> (with optional
    /// flags), where <c>{base}</c> is the main file's stem. The match is exact
    /// on <c>{base}.</c> so a movie named <c>Foo.mkv</c> never picks up
    /// companions of a sibling like <c>Foo 2.mkv</c>.
    /// </summary>
    /// <param name="mainFilePath">Path to the main file.</param>
    /// <returns>List of companion file paths.</returns>
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
                // Use a coarse glob on the prefix, then filter strictly so we
                // only accept "{base}.<...>{ext}" — not "{base} 2{ext}" etc.
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
                // Directory access error during companion file search
            }
        }

        return companions;
    }

    /// <summary>
    /// Tests if a directory has write permissions by creating a temporary file.
    /// </summary>
    /// <param name="directoryPath">Directory path to test.</param>
    /// <returns>True if write permission exists.</returns>
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
                        // Test file cleanup failed; DeleteOnClose should handle it
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
