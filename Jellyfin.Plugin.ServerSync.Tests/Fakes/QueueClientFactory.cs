using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Clients;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Builds a real client over a <see cref="QueuePeerHandler"/> for each server key, refuses the keys a test names, and records the keys it was asked for.
/// </summary>
public sealed class QueueClientFactory : ISourceServerClientFactory
{
    private readonly Dictionary<string, string> _refused = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the peer serving each server key.</summary>
    public Dictionary<string, QueuePeerHandler> Peers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the server keys a client was asked for, in order.</summary>
    public List<string> Created { get; } = new();

    /// <summary>Gives the peer for a server key, adding one when none exists.</summary>
    /// <param name="key">The server key.</param>
    /// <returns>The peer.</returns>
    public QueuePeerHandler Peer(string key)
    {
        if (!Peers.TryGetValue(key, out var peer))
        {
            peer = new QueuePeerHandler();
            Peers[key] = peer;
        }

        return peer;
    }

    /// <summary>Refuses to build a client for a server key, the way an address the SSRF guard rejects is refused.</summary>
    /// <param name="key">The server key to refuse.</param>
    /// <param name="message">Why the address is refused.</param>
    public void Refuse(string key, string message) => _refused[key] = message;

    /// <inheritdoc/>
    public SourceServerClient Create(SourceServer server)
    {
        Created.Add(server.Key);
        if (_refused.TryGetValue(server.Key, out var message))
        {
            throw new ArgumentException(message);
        }

        return Peer(server.Key).Client(server.Url);
    }
}
