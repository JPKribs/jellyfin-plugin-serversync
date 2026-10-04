using Jellyfin.Plugin.ServerSync.Services.Peer;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// The pairing a client holds with one configured peer: where its secret lives and which server this
/// is, so the client can present the secret and pair again when the peer no longer holds it.
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
        _store = store;
        _peerKey = peerKey;
        ThisServerId = thisServerId;
    }

    /// <summary>Gets this server's id.</summary>
    public string ThisServerId { get; }

    /// <summary>The secret the peer issued to this server, or null when it never did.</summary>
    /// <returns>The secret.</returns>
    public string? CurrentSecret() => _store.GetOutbound(_peerKey);
}
