using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

public static class ProviderFills
{
    public static readonly TimeSpan Remembered = TimeSpan.FromMinutes(5);

    private static readonly ConcurrentDictionary<string, DateTime> Keys = new(StringComparer.OrdinalIgnoreCase);

    public static void MarkPath(string? path) => Mark(string.IsNullOrEmpty(path) ? null : "path:" + path.Replace('\\', '/'));

    public static void MarkPerson(string? name) => Mark(string.IsNullOrWhiteSpace(name) ? null : HintTriage.PersonParkKey(name));

    public static bool WasFilled(string? path, string? personName, DateTime utcNow)
        => Was(string.IsNullOrEmpty(path) ? null : "path:" + path.Replace('\\', '/'), utcNow)
           || Was(string.IsNullOrWhiteSpace(personName) ? null : HintTriage.PersonParkKey(personName), utcNow);

    public static void Clear() => Keys.Clear();

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

    private static bool Was(string? key, DateTime utcNow)
        => key is not null && Keys.TryGetValue(key, out var at) && utcNow - at < Remembered;
}
