using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Replaces characters and patterns that are illegal in cross-platform file names.
/// </summary>
public static partial class FileNameSanitizer
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    private static readonly char[] AdditionalInvalidChars = { ':', '*', '?', '"', '<', '>', '|', '\0' };

    private static readonly HashSet<char> AllInvalidChars = InvalidChars
        .Concat(AdditionalInvalidChars)
        .Distinct()
        .ToHashSet();

    private static readonly Regex UnderscoreCollapseRegex = new(@"_+", RegexOptions.Compiled);

    private const int MaxFileNameLength = 200;

    /// <summary>
    /// Returns a cross-platform-safe file name. Empty or null input becomes "unnamed_{guid}".
    /// </summary>
    public static string Sanitize(string? fileName, char replacement = '_')
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return $"unnamed_{Guid.NewGuid():N}";
        }

        var sb = new StringBuilder(fileName.Length);

        foreach (var c in fileName)
        {
            if (AllInvalidChars.Contains(c) || char.IsControl(c))
            {
                sb.Append(replacement);
            }
            else
            {
                sb.Append(c);
            }
        }

        var sanitized = sb.ToString();
        sanitized = CollapseReplacements(sanitized, replacement);
        sanitized = sanitized.Trim(' ', '.');
        sanitized = HandleReservedNames(sanitized);

        if (sanitized.Length > MaxFileNameLength)
        {
            sanitized = TruncateWithExtension(sanitized, MaxFileNameLength);
        }

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return $"unnamed_{Guid.NewGuid():N}";
        }

        return sanitized;
    }

    /// <summary>
    /// Sanitizes a temp file name as "{sanitized-id}_{sanitized-original-name}".
    /// </summary>
    public static string SanitizeTempFileName(string sourceItemId, string? originalPath)
    {
        var originalName = !string.IsNullOrEmpty(originalPath)
            ? Path.GetFileName(originalPath)
            : null;

        var sanitizedName = Sanitize(originalName);
        var sanitizedId = Sanitize(sourceItemId);

        return $"{sanitizedId}_{sanitizedName}";
    }

    private static string CollapseReplacements(string input, char replacement)
    {
        if (replacement == '_')
        {
            return UnderscoreCollapseRegex.Replace(input, "_");
        }

        var pattern = $"{Regex.Escape(replacement.ToString())}+";
        return Regex.Replace(input, pattern, replacement.ToString());
    }

    private static string HandleReservedNames(string fileName)
    {
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName).ToUpperInvariant();
        var extension = Path.GetExtension(fileName);

        string[] reservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        if (reservedNames.Contains(nameWithoutExtension))
        {
            return $"{Path.GetFileNameWithoutExtension(fileName)}_{extension}";
        }

        return fileName;
    }

    private static string TruncateWithExtension(string fileName, int maxLength)
    {
        var extension = Path.GetExtension(fileName);
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);

        var maxNameLength = maxLength - extension.Length;
        if (maxNameLength < 1)
        {
            return fileName[..maxLength];
        }

        if (nameWithoutExtension.Length > maxNameLength)
        {
            nameWithoutExtension = nameWithoutExtension[..maxNameLength];
        }

        return nameWithoutExtension + extension;
    }
}
