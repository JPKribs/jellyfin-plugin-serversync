#pragma warning disable CA2100 // SQL is internal and parameterized.
using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Peer;

/// <summary>
/// Holds the pairing secrets that tie a peer's requests to the server entry they claim to come from.
/// This server issues one to a peer over its own connection, and the peer presents it on every request that names it as the sender.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class PeerPairingStore : QueueStoreBase
{
    private readonly SecretProtector _secrets;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pairing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the lock that keeps two pairings with one entry from each minting a secret the other side does not keep.
    /// </summary>
    /// <param name="peerKey">The server entry's key.</param>
    /// <returns>The entry's lock.</returns>
    public SemaphoreSlim PairingLock(string peerKey) => _pairing.GetOrAdd(peerKey, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerPairingStore"/> class.
    /// </summary>
    /// <param name="databaseProvider">Opens the database the secrets are stored in.</param>
    /// <param name="secrets">Protects the secrets at rest.</param>
    /// <param name="logger">Writes database failures.</param>
    public PeerPairingStore(ISyncDatabaseProvider databaseProvider, SecretProtector secrets, ILogger<PeerPairingStore> logger)
        : base(databaseProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = secrets;
    }

    private readonly ConcurrentDictionary<string, int> _expected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Notes that this server is asking an entry to pair, until the result is disposed.
    /// A secret that arrives at any other time was not asked for and is refused.
    /// </summary>
    /// <param name="peerKey">The server entry's key.</param>
    /// <returns>Ends the wait when disposed.</returns>
    public IDisposable Expect(string peerKey)
    {
        _expected.AddOrUpdate(peerKey, 1, (_, open) => open + 1);
        return new Expectation(this, peerKey);
    }

    /// <summary>Whether this server has an open question asking an entry to pair.</summary>
    /// <param name="peerKey">The server entry's key.</param>
    /// <returns><c>true</c> while a question to it is open.</returns>
    public bool Expecting(string peerKey) => _expected.TryGetValue(peerKey, out var open) && open > 0;

    /// <summary>Whether a value has the shape of a secret from <see cref="NewSecret"/>, which is URL safe text a header can carry.</summary>
    /// <param name="secret">The value a peer handed over.</param>
    /// <returns><c>true</c> when it could be a pairing secret.</returns>
    public static bool WellFormed(string? secret)
    {
        if (secret is null || secret.Length is < 32 or > 256)
        {
            return false;
        }

        foreach (var c in secret)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Makes a fresh secret.</summary>
    /// <returns>The secret, URL safe.</returns>
    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Whether a presented secret matches an expected one, compared in constant time.</summary>
    /// <param name="presented">What the request carried.</param>
    /// <param name="expected">What this server issued.</param>
    /// <returns><c>true</c> when they match.</returns>
    public static bool Matches(string? presented, string? expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        return SecretCompare.FixedTimeEquals(presented, expected, StringComparison.Ordinal);
    }

    /// <summary>The secret a peer must present when it names itself as the sender, or null when it was never issued one.</summary>
    /// <param name="peerKey">The server entry's key.</param>
    /// <returns>The secret.</returns>
    public string? GetInbound(string peerKey) => Unprotect(ReadColumn(peerKey, "InboundSecret"));

    /// <summary>The secret this server presents to a peer, or null when the peer never issued one.</summary>
    /// <param name="peerKey">The server entry's key.</param>
    /// <returns>The secret.</returns>
    public string? GetOutbound(string peerKey) => Unprotect(ReadColumn(peerKey, "OutboundSecret"));

    /// <summary>Stores the secret a peer must present to this server.</summary>
    /// <param name="peerKey">The server entry's key.</param>
    /// <param name="secret">The secret this server issued.</param>
    public void SetInbound(string peerKey, string secret) => Upsert(peerKey, "InboundSecret", secret);

    /// <summary>Stores the secret this server presents to a peer.</summary>
    /// <param name="peerKey">The server entry's key.</param>
    /// <param name="secret">The secret the peer issued.</param>
    public void SetOutbound(string peerKey, string secret) => Upsert(peerKey, "OutboundSecret", secret);

    /// <summary>Forgets both secrets for a server entry, for when the entry is removed.</summary>
    /// <param name="peerKey">The server entry's key.</param>
    public void Remove(string peerKey) => Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM PeerPairings WHERE PeerKey = @peer";
        Add(cmd, "@peer", peerKey);
        cmd.ExecuteNonQuery();
    });

    /// <summary>One open question to an entry, which closes when disposed.</summary>
    private sealed class Expectation : IDisposable
    {
        private readonly PeerPairingStore _store;
        private readonly string _peerKey;
        private int _done;

        /// <summary>
        /// Initializes a new instance of the <see cref="Expectation"/> class.
        /// </summary>
        public Expectation(PeerPairingStore store, string peerKey)
        {
            _store = store;
            _peerKey = peerKey;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                _store._expected.AddOrUpdate(_peerKey, 0, (_, open) => Math.Max(0, open - 1));
            }
        }
    }

    /// <summary>Decrypts a stored secret, reading one that cannot be decrypted as none so the next pairing issues a fresh one.</summary>
    private string? Unprotect(string? stored)
    {
        var plain = string.IsNullOrEmpty(stored) ? null : _secrets.Unprotect(stored);
        return string.IsNullOrEmpty(plain) ? null : plain;
    }

    /// <summary>Reads one secret column for an entry.</summary>
    private string? ReadColumn(string peerKey, string column) => Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM PeerPairings WHERE PeerKey = @peer";
        Add(cmd, "@peer", peerKey);
        var value = cmd.ExecuteScalar();
        return value is string text ? text : null;
    });

    /// <summary>Encrypts a secret and stores it in one column for an entry.</summary>
    private void Upsert(string peerKey, string column, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var stored = _secrets.Protect(secret);
        Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                INSERT INTO PeerPairings (PeerKey, {column}, UpdatedAt) VALUES (@peer, @secret, @at)
                ON CONFLICT(PeerKey) DO UPDATE SET {column} = @secret, UpdatedAt = @at";
            Add(cmd, "@peer", peerKey);
            Add(cmd, "@secret", stored);
            Add(cmd, "@at", Stamp(DateTime.UtcNow));
            cmd.ExecuteNonQuery();
        });
    }
}
