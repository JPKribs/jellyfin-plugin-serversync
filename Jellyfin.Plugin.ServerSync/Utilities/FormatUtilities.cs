using System;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Utility methods for formatting values.
/// </summary>
public static class FormatUtilities
{
    /// <summary>
    /// Formats bytes to human-readable string.
    /// </summary>
    /// <param name="bytes">The number of bytes to format.</param>
    /// <returns>A formatted string like "1.50 GB".</returns>
    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:F2} {units[unitIndex]}";
    }

    /// <summary>
    /// Truncates a string for inclusion in a log line. Long JSON blobs in
    /// log messages bloat the log without adding useful information beyond
    /// the first ~200 chars; this caps and adds a horizontal-ellipsis.
    /// Empty/null inputs render as "(empty)" so the log line stays readable.
    /// </summary>
    /// <param name="s">The string to truncate.</param>
    /// <param name="maxLength">Maximum length before truncation; default 200.</param>
    /// <returns>Original string, "(empty)", or truncated form with ellipsis.</returns>
    public static string TruncateForLog(string? s, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(s)) return "(empty)";
        return s.Length <= maxLength ? s : string.Concat(s.AsSpan(0, maxLength), "…");
    }
}
