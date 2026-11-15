using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Remembers which objects are being written because a peer asked for it.
/// The change observer asks the guard whether a save event is the echo of an apply, and stays quiet when it is.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class ApplyGuard
{
    private static readonly ConcurrentDictionary<string, DateTime> RecentApplies = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, int> _active = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether an apply of this object, or of its whole kind, ended within the window.
    /// </summary>
    /// <param name="guardKey">The guard key of the object the save event names.</param>
    /// <param name="window">How far back an apply counts.</param>
    /// <returns><c>true</c> when an apply ended within the window.</returns>
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

    /// <summary>Registers an apply of one object until the result is disposed.</summary>
    /// <param name="guardKey">The guard key, see <see cref="HintProtocol.GuardKey"/>.</param>
    /// <returns>A handle that ends the registration.</returns>
    public IDisposable Enter(string guardKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(guardKey);
        _active.AddOrUpdate(guardKey, 1, (_, count) => count + 1);
        return new Exit(this, guardKey);
    }

    /// <summary>Whether an apply of this object is in progress.</summary>
    /// <param name="guardKey">The guard key of the object the save event names.</param>
    /// <returns><c>true</c> during an apply.</returns>
    public bool IsApplying(string guardKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(guardKey);
        if (_active.ContainsKey(guardKey))
        {
            return true;
        }

        // A registration for a whole kind covers every key of that kind.
        var split = guardKey.IndexOf(':', StringComparison.Ordinal);
        return split > 0 && _active.ContainsKey(string.Concat(guardKey.AsSpan(0, split + 1), "*"));
    }

    /// <summary>Ends one registration and remembers when the apply ended.</summary>
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
                if (_active.TryRemove(new KeyValuePair<string, int>(guardKey, count)))
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

    /// <summary>Ends a registration once when disposed.</summary>
    private sealed class Exit : IDisposable
    {
        private readonly ApplyGuard _guard;
        private readonly string _key;
        private bool _done;

        /// <summary>
        /// Initializes a new instance of the <see cref="Exit"/> class.
        /// </summary>
        public Exit(ApplyGuard guard, string key)
        {
            _guard = guard;
            _key = key;
        }

        /// <inheritdoc />
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
