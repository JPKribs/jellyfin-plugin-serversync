using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Remembers which objects are being written right now because a peer asked for it. Jellyfin raises
/// its save events on the writing thread, so the change observer can ask the guard whether an event
/// is the echo of an apply and stay quiet. Nothing here outlives the write.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class ApplyGuard
{
    private static readonly ConcurrentDictionary<string, DateTime> RecentApplies = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, int> _active = new(StringComparer.Ordinal);

    public static bool AppliedRecently(string guardKey, TimeSpan window)
    {
        ArgumentException.ThrowIfNullOrEmpty(guardKey);
        if (RecentApplies.TryGetValue(guardKey, out var at) && DateTime.UtcNow - at < window)
        {
            return true;
        }

        var split = guardKey.IndexOf(':', StringComparison.Ordinal);
        return split > 0
            && RecentApplies.TryGetValue(string.Concat(guardKey.AsSpan(0, split + 1), "*"), out var all)
            && DateTime.UtcNow - all < window;
    }

    /// <summary>Registers an apply of one object. Dispose the result when the write is done.</summary>
    /// <param name="guardKey">The guard key, see <see cref="HintProtocol.GuardKey"/>.</param>
    /// <returns>A handle that ends the registration.</returns>
    public IDisposable Enter(string guardKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(guardKey);
        _active.AddOrUpdate(guardKey, 1, (_, count) => count + 1);
        return new Exit(this, guardKey);
    }

    /// <summary>Whether an apply of this object is in progress.</summary>
    /// <param name="guardKey">The guard key.</param>
    /// <returns><c>true</c> during an apply.</returns>
    public bool IsApplying(string guardKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(guardKey);
        if (_active.ContainsKey(guardKey))
        {
            return true;
        }

        // A registration for a whole kind, see HintProtocol.GuardAllKey, covers every key of that kind.
        var split = guardKey.IndexOf(':', StringComparison.Ordinal);
        return split > 0 && _active.ContainsKey(string.Concat(guardKey.AsSpan(0, split + 1), "*"));
    }

    private void Leave(string guardKey)
    {
        RecentApplies[guardKey] = DateTime.UtcNow;
        if (RecentApplies.Count > 10000)
        {
            foreach (var (key, at) in RecentApplies)
            {
                if (DateTime.UtcNow - at > TimeSpan.FromHours(1))
                {
                    RecentApplies.TryRemove(key, out _);
                }
            }
        }

        while (_active.TryGetValue(guardKey, out var count))
        {
            if (count <= 1)
            {
                if (_active.TryRemove(new System.Collections.Generic.KeyValuePair<string, int>(guardKey, count)))
                {
                    return;
                }
            }
            else if (_active.TryUpdate(guardKey, count - 1, count))
            {
                return;
            }
        }
    }

    private sealed class Exit : IDisposable
    {
        private readonly ApplyGuard _guard;
        private readonly string _key;
        private bool _done;

        public Exit(ApplyGuard guard, string key)
        {
            _guard = guard;
            _key = key;
        }

        public void Dispose()
        {
            if (!_done)
            {
                _done = true;
                _guard.Leave(_key);
            }
        }
    }
}
