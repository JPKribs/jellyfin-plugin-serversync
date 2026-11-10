using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// The link streams this server is serving, for its limits. A player reads a file in many requests,
/// with gaps while it buffers or seeks, so a stream counts while a request is open and for a short
/// while after the last one ends.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkSessions
{
    /// <summary>How long a stream still counts after its last request ended.</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(90);

    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private readonly Dictionary<(string Peer, Guid User, Guid Item), Stream> _streams = new();

    public LinkSessions()
        : this(() => DateTime.UtcNow)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LinkSessions"/> class, for tests.</summary>
    /// <param name="now">The clock.</param>
    public LinkSessions(Func<DateTime> now)
    {
        _now = now;
    }

    /// <summary>Marks a request for a stream as open until the returned handle is disposed.</summary>
    public IDisposable Open(string peerKey, Guid userId, Guid itemId)
    {
        var key = (peerKey.ToUpperInvariant(), userId, itemId);
        lock (_lock)
        {
            if (!_streams.TryGetValue(key, out var stream))
            {
                stream = new Stream();
                _streams[key] = stream;
            }

            stream.Open++;
            stream.LastSeen = _now();
        }

        return new Handle(this, key);
    }

    /// <summary>
    /// Opens a request for a stream when the limits allow it, checking and opening under one lock so two
    /// first requests at once cannot both slip under a limit. A stream already running always opens.
    /// Null when a limit is reached.
    /// </summary>
    /// <param name="peerKey">The calling server's entry key.</param>
    /// <param name="userId">The user streamed as.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="maxForPeer">The calling server's limit, or 0 for none.</param>
    /// <param name="maxForUser">The user's limit, or 0 for none.</param>
    /// <param name="userElsewhere">Streams the user holds outside links, counted toward their limit.</param>
    /// <returns>The handle, or null.</returns>
    public IDisposable? TryOpen(string peerKey, Guid userId, Guid itemId, int maxForPeer, int maxForUser, int userElsewhere)
    {
        var peer = peerKey.ToUpperInvariant();
        lock (_lock)
        {
            Prune();
            if (!_streams.ContainsKey((peer, userId, itemId))
                && ((maxForPeer > 0 && _streams.Keys.Count(k => k.Peer == peer) >= maxForPeer)
                    || (maxForUser > 0 && _streams.Keys.Count(k => k.User == userId) + userElsewhere >= maxForUser)))
            {
                return null;
            }

            return Open(peerKey, userId, itemId);
        }
    }

    /// <summary>Whether this user already streams this item to that server, so a new request is not a new stream.</summary>
    public bool IsActive(string peerKey, Guid userId, Guid itemId)
    {
        lock (_lock)
        {
            Prune();
            return _streams.ContainsKey((peerKey.ToUpperInvariant(), userId, itemId));
        }
    }

    /// <summary>The streams that server holds.</summary>
    public int CountForPeer(string peerKey)
    {
        var peer = peerKey.ToUpperInvariant();
        lock (_lock)
        {
            Prune();
            return _streams.Keys.Count(k => k.Peer == peer);
        }
    }

    /// <summary>The streams one user holds, through any server.</summary>
    public int CountForUser(Guid userId)
    {
        lock (_lock)
        {
            Prune();
            return _streams.Keys.Count(k => k.User == userId);
        }
    }

    private void Close((string, Guid, Guid) key)
    {
        lock (_lock)
        {
            if (_streams.TryGetValue(key, out var stream))
            {
                stream.Open = Math.Max(0, stream.Open - 1);
                stream.LastSeen = _now();
            }
        }
    }

    private void Prune()
    {
        var cutoff = _now() - Linger;
        foreach (var key in _streams.Where(p => p.Value.Open == 0 && p.Value.LastSeen < cutoff).Select(p => p.Key).ToList())
        {
            _streams.Remove(key);
        }
    }

    private sealed class Stream
    {
        public int Open { get; set; }

        public DateTime LastSeen { get; set; }
    }

    private sealed class Handle : IDisposable
    {
        private readonly LinkSessions _owner;
        private readonly (string, Guid, Guid) _key;
        private bool _disposed;

        public Handle(LinkSessions owner, (string, Guid, Guid) key)
        {
            _owner = owner;
            _key = key;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _owner.Close(_key);
            }
        }
    }
}
