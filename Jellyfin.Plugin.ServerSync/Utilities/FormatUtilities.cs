using System;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Formatting helpers for log lines and user-facing strings.
/// </summary>
public static class FormatUtilities
{
    /// <summary>
    /// Formats a byte count as a human-readable string (e.g. "1.50 GB").
    /// </summary>
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
    /// Truncates a string for log output. Empty or null renders as "(empty)".
    /// </summary>
    public static string TruncateForLog(string? s, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(s)) return "(empty)";
        return s.Length <= maxLength ? s : string.Concat(s.AsSpan(0, maxLength), "…");
    }
}
