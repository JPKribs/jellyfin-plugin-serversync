using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.Queue;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

public static class LoopBreaker
{
    public const int Limit = 10;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, Queue<DateTime>> Announced = new(StringComparer.Ordinal);

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

    public static void Reset() => Announced.Clear();
}
