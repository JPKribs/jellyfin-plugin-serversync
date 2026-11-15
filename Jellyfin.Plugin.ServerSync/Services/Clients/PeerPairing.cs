using System;
using Jellyfin.Plugin.ServerSync.Services.Peer;

namespace Jellyfin.Plugin.ServerSync.Services.Clients;

/// <summary>
/// The pairing a client holds with one peer, which lets it send the secret and pair again when the peer
/// has lost it.
/// </summary>
public sealed class PeerPairing
{
    private readonly PeerPairingStore _store;
    private readonly string _peerKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerPairing"/> class.
    /// </summary>
    /// <param name="store">Where the secrets live.</param>
    /// <param name="peerKey">The server entry's key.</param>
    /// <param name="thisServerId">This server's id, which the peer pairs against.</param>
    public PeerPairing(PeerPairingStore store, string peerKey, string thisServerId)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(peerKey);
        ArgumentNullException.ThrowIfNull(thisServerId);

        _store = store;
        _peerKey = peerKey;
        ThisServerId = thisServerId;
    }

    /// <summary>Gets this server's id.</summary>
    public string ThisServerId { get; }

    /// <summary>Reads the secret the peer issued to this server.</summary>
    /// <returns>The secret, or null when the peer never issued one.</returns>
    public string? CurrentSecret() => _store.GetOutbound(_peerKey);

    /// <summary>Notes that this server is asking the peer to pair, until the result is disposed.</summary>
    /// <returns>Ends the wait when disposed.</returns>
    public IDisposable Expect() => _store.Expect(_peerKey);
}
