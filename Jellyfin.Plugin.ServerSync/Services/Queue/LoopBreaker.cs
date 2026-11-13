using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.Queue;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Stops announcing an object that keeps changing right after applies, which looks like two servers
/// echoing one change back and forth.
/// </summary>
public static class LoopBreaker
{
    /// <summary>The most announcements of one object within the window.</summary>
    public const int Limit = 10;

    /// <summary>The window announcements are counted over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, Queue<DateTime>> Announced = new(StringComparer.Ordinal);

    /// <summary>Counts an announcement of an object and says whether it is one too many.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="localKey">This server's key.</param>
    /// <param name="utcNow">The current time.</param>
    /// <returns><c>true</c> when the object was announced too often and this one is held back.</returns>
    public static bool Trips(HintKind kind, string localKey, DateTime utcNow)
    {
        var times = Announced.GetOrAdd(HintProtocol.GuardKey(kind, localKey), _ => new Queue<DateTime>());
        lock (times)
        {
            while (times.Count > 0 && utcNow - times.Peek() > Window)
            {
                times.Dequeue();
            }

            if (times.Count >= Limit)
            {
                return true;
            }

            times.Enqueue(utcNow);
        }

        if (Announced.Count > 10000)
        {
            foreach (var (key, queue) in Announced)
            {
                lock (queue)
                {
                    if (queue.Count == 0 || utcNow - queue.Peek() > Window)
                    {
                        Announced.TryRemove(key, out _);
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Forgets every count.</summary>
    public static void Reset() => Announced.Clear();
}
