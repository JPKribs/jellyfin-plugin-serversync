using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// The files this server wrote itself while syncing content. Jellyfin picks such a file up in a later
/// library scan, outside any apply guard, and the change observer would announce it to every peer as a
/// new file, its origin included. The observer checks here and stays quiet about files it wrote.
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
    /// <returns>True when it was.</returns>
    public static bool WasWritten(string? path)
        => !string.IsNullOrEmpty(path)
           && Paths.TryGetValue(Normalize(path), out var at)
           && DateTime.UtcNow - at < Remembered;

    private static string Normalize(string path) => path.Replace('\\', '/');
}
