using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Remembers the objects a peer said it will not take, for example an unmapped path, so they are not
/// sent to it again for a day, or until the peer's settings change.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class HintSuppression
{
    /// <summary>How long a declined object is held back.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromHours(24);

    /// <summary>The most objects held back at once.</summary>
    public const int MaxHeld = 100_000;

    private readonly ConcurrentDictionary<string, (DateTime Until, string? Revision, HintCode Code)> _held = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets how many objects are held back.</summary>
    public int Count => _held.Count;

    // NoLocalItem is not held back: the item may still arrive after the peer stopped waiting for it,
    // and holding the key would silence its first real edits there for a whole day.
    /// <summary>Whether a peer's answer means it will not take the object until its settings change.</summary>
    /// <param name="code">The peer's answer.</param>
    /// <returns><c>true</c> when the object is held back.</returns>
    public static bool Suppresses(HintCode code) => code is HintCode.NotMapped
        or HintCode.Filtered
        or HintCode.Ignored
        or HintCode.ModuleOff
        or HintCode.Unsupported
        or HintCode.Priority;

    /// <summary>Holds an object back from a peer when the peer's answer declines it.</summary>
    /// <param name="peerKey">The peer's entry key.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="key">This server's key for the object.</param>
    /// <param name="code">The peer's answer.</param>
    /// <param name="revision">The revision of the peer's settings the answer was given under.</param>
    /// <param name="utcNow">The current time.</param>
    /// <returns><c>true</c> when the object is now held back.</returns>
    public bool Note(string peerKey, HintKind kind, string key, HintCode code, string? revision, DateTime utcNow)
    {
        if (!Suppresses(code) || string.IsNullOrEmpty(peerKey) || string.IsNullOrEmpty(key))
        {
            return false;
        }

        if (_held.Count >= MaxHeld)
        {
            foreach (var expired in _held.Where(p => p.Value.Until <= utcNow).Select(p => p.Key).ToList())
            {
                _held.TryRemove(expired, out _);
            }

            if (_held.Count >= MaxHeld)
            {
                _held.Clear();
            }
        }

        _held[Name(peerKey, kind, key)] = (utcNow + Hold, revision, code);
        return true;
    }

    /// <summary>Whether an object is held back from a peer, at the peer's current settings.</summary>
    /// <param name="peerKey">The peer's entry key.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="key">This server's key for the object.</param>
    /// <param name="revision">The revision of the peer's current settings.</param>
    /// <param name="utcNow">The current time.</param>
    /// <returns><c>true</c> when the hint should not be sent.</returns>
    public bool IsHeld(string peerKey, HintKind kind, string key, string? revision, DateTime utcNow)
    {
        var name = Name(peerKey, kind, key);
        if (!_held.TryGetValue(name, out var held))
        {
            return false;
        }

        if (held.Until <= utcNow || !string.Equals(held.Revision, revision, StringComparison.Ordinal))
        {
            _held.TryRemove(name, out _);
            return false;
        }

        return true;
    }

    /// <summary>Forgets every held object.</summary>
    public void Clear() => _held.Clear();

    /// <summary>Forgets the objects held back from one peer.</summary>
    /// <param name="peerKey">The peer's entry key.</param>
    /// <returns>How many were forgotten.</returns>
    public int ClearPeer(string peerKey)
    {
        var prefix = peerKey + "|";
        var removed = 0;
        foreach (var name in _held.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (_held.TryRemove(name, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    private static string Name(string peerKey, HintKind kind, string key)
        => peerKey + "|" + ((int)kind).ToString(CultureInfo.InvariantCulture) + "|" + (kind == HintKind.People ? HintProtocol.PeopleKey(key) : key);
}
