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

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkHealth"/> class.
    /// </summary>
    public LinkHealth()
        : this(() => DateTime.UtcNow)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkHealth"/> class.
    /// </summary>
    /// <param name="now">Reads the current time, so a test can move it.</param>
    public LinkHealth(Func<DateTime> now)
    {
        ArgumentNullException.ThrowIfNull(now);
        _now = now;
    }

    /// <summary>Records that a server did not answer.</summary>
    /// <param name="serverKey">The key of the server entry that did not answer.</param>
    public void Failed(string serverKey) => _failedAt[serverKey ?? string.Empty] = _now();

    /// <summary>Records that a server answered, whatever it said.</summary>
    /// <param name="serverKey">The key of the server entry that answered.</param>
    public void Answered(string serverKey) => _failedAt.TryRemove(serverKey ?? string.Empty, out _);

    /// <summary>Whether a server failed to answer within the last minute.</summary>
    /// <param name="serverKey">The key of the server entry to check, or null.</param>
    /// <returns><c>true</c> when it did not answer.</returns>
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
