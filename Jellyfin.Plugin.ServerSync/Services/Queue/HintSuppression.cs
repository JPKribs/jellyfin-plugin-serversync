using System;
using System.Collections.Concurrent;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

[PluginService(ServiceLifetime.Singleton)]
public sealed class HintSuppression
{
    public static readonly TimeSpan Hold = TimeSpan.FromHours(24);

    public const int MaxHeld = 100_000;

    private readonly ConcurrentDictionary<string, (DateTime Until, string? Revision, HintCode Code)> _held = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _held.Count;

    // NoLocalItem is not held back: the item may still arrive after the peer stopped waiting for it,
    // and holding the key would silence its first real edits there for a whole day.
    public static bool Suppresses(HintCode code) => code is HintCode.NotMapped
        or HintCode.Filtered
        or HintCode.Ignored
        or HintCode.ModuleOff
        or HintCode.Unsupported
        or HintCode.Priority;

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

    public void Clear() => _held.Clear();

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
        => peerKey + "|" + ((int)kind).ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + (kind == HintKind.People ? HintProtocol.PeopleKey(key) : key);
}
