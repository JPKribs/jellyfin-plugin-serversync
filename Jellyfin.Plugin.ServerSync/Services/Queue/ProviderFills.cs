using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Remembers the items and people this server's metadata provider filled from a source.
/// The change observer checks here so a value copied from a source is not announced back to the pool.
/// </summary>
public static class ProviderFills
{
    /// <summary>How long a fill is remembered.</summary>
    public static readonly TimeSpan Remembered = TimeSpan.FromMinutes(5);

    private static readonly ConcurrentDictionary<string, DateTime> Keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Notes that the provider filled the item at a path.</summary>
    /// <param name="path">The item's local path.</param>
    public static void MarkPath(string? path) => Mark(string.IsNullOrEmpty(path) ? null : "path:" + path.Replace('\\', '/'));

    /// <summary>Notes that the provider filled a person.</summary>
    /// <param name="name">The person's name.</param>
    public static void MarkPerson(string? name) => Mark(string.IsNullOrWhiteSpace(name) ? null : HintTriage.PersonParkKey(name));

    /// <summary>Whether the provider filled the item at a path, or the person, within the remembered time.</summary>
    /// <param name="path">The item's local path.</param>
    /// <param name="personName">The person's name, for a person.</param>
    /// <param name="utcNow">The time the fill is measured against.</param>
    /// <returns><c>true</c> when it was.</returns>
    public static bool WasFilled(string? path, string? personName, DateTime utcNow)
        => Was(string.IsNullOrEmpty(path) ? null : "path:" + path.Replace('\\', '/'), utcNow)
           || Was(string.IsNullOrWhiteSpace(personName) ? null : HintTriage.PersonParkKey(personName), utcNow);

    /// <summary>Forgets every fill.</summary>
    public static void Clear() => Keys.Clear();

    /// <summary>Remembers a key, and drops expired keys once the list grows large.</summary>
    private static void Mark(string? key)
    {
        if (key is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        Keys[key] = now;
        if (Keys.Count > 20000)
        {
            foreach (var old in Keys.Where(p => now - p.Value > Remembered).Select(p => p.Key).ToList())
            {
                Keys.TryRemove(old, out _);
            }
        }
    }

    /// <summary>Whether a key was marked within the remembered time.</summary>
    private static bool Was(string? key, DateTime utcNow)
        => key is not null && Keys.TryGetValue(key, out var at) && utcNow - at < Remembered;
}
