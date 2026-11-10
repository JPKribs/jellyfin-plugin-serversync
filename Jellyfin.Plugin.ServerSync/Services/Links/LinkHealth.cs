using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Remembers for a short while which servers could not be reached for a link, so the next play does not
/// wait out the same timeout before trying another server. Only a server that did not answer is
/// remembered: a refusal is an answer, and is asked for again on every play.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkHealth
{
    /// <summary>How long a server that did not answer is passed over.</summary>
    public static readonly TimeSpan Remembered = TimeSpan.FromMinutes(1);

    private readonly Func<DateTime> _now;
    private readonly ConcurrentDictionary<string, DateTime> _failedAt = new(StringComparer.OrdinalIgnoreCase);

    public LinkHealth()
        : this(() => DateTime.UtcNow)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LinkHealth"/> class, for tests.</summary>
    /// <param name="now">The clock.</param>
    public LinkHealth(Func<DateTime> now)
    {
        _now = now;
    }

    /// <summary>Records that a server did not answer.</summary>
    public void Failed(string serverKey) => _failedAt[serverKey ?? string.Empty] = _now();

    /// <summary>Records that a server answered, whatever it said.</summary>
    public void Answered(string serverKey) => _failedAt.TryRemove(serverKey ?? string.Empty, out _);

    /// <summary>Whether a server failed to answer within the last minute.</summary>
    public bool IsDown(string? serverKey)
    {
        if (string.IsNullOrEmpty(serverKey) || !_failedAt.TryGetValue(serverKey, out var at))
        {
            return false;
        }

        if (_now() - at < Remembered)
        {
            return true;
        }

        _failedAt.TryRemove(serverKey, out _);
        return false;
    }
}
