using System;
using System.Globalization;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// The rules for times the plugin stores or compares, where a time with no zone is UTC and a local time is converted.
/// Stored times are UTC round trip text ending in Z, so SQL compares them in time order as text.
/// </summary>
public static class UtcTime
{
    /// <summary>Returns the time as UTC, reading a time with no zone as UTC rather than as local.</summary>
    /// <param name="value">The time to read as UTC.</param>
    /// <returns>The same instant with a UTC kind.</returns>
    public static DateTime AsUtc(DateTime value)
        => value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();

    /// <summary>Formats a time the way every table stores it.</summary>
    /// <param name="value">The time to store.</param>
    /// <returns>UTC round trip text ending in Z.</returns>
    public static string Format(DateTime value) => AsUtc(value).ToString("o", CultureInfo.InvariantCulture);

    /// <summary>Parses stored text into a UTC time. Text with no zone is read as UTC.</summary>
    /// <param name="value">The stored text.</param>
    /// <param name="result">The UTC time, or <see cref="DateTime.MinValue"/> when the text is not a time.</param>
    /// <returns><c>true</c> when the text is a time.</returns>
    public static bool TryParse(string? value, out DateTime result)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            result = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }

        result = DateTime.MinValue;
        return false;
    }

    /// <summary>Parses stored text into a UTC time, throwing when it is not a time.</summary>
    /// <param name="value">The stored text.</param>
    /// <returns>The UTC time.</returns>
    public static DateTime Parse(string value)
        => TryParse(value, out var result) ? result : throw new FormatException($"'{value}' is not a time");
}
