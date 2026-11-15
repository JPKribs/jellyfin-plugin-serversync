using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Remembers the files this server wrote while syncing content, so the change observer stays quiet about them.
/// Jellyfin picks such a file up outside any apply guard, and it would be announced to every peer as a new file.
/// </summary>
public static class WrittenFiles
{
    private static readonly TimeSpan Remembered = TimeSpan.FromDays(1);
    private static readonly ConcurrentDictionary<string, DateTime> Paths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Notes that a file was written by a sync.</summary>
    /// <param name="path">The file's local path.</param>
    public static void Mark(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var now = DateTime.UtcNow;
        Paths[Normalize(path)] = now;
        if (Paths.Count > 5000)
        {
            foreach (var old in Paths.Where(p => now - p.Value > Remembered).Select(p => p.Key).ToList())
            {
                Paths.TryRemove(old, out _);
            }
        }
    }

    /// <summary>Whether a file was written by a sync within the last day.</summary>
    /// <param name="path">The file's local path.</param>
    /// <returns><c>true</c> when it was.</returns>
    public static bool WasWritten(string? path)
        => !string.IsNullOrEmpty(path)
           && Paths.TryGetValue(Normalize(path), out var at)
           && DateTime.UtcNow - at < Remembered;

    /// <summary>Writes a path with forward slashes so both separators compare equal.</summary>
    private static string Normalize(string path) => path.Replace('\\', '/');
}
