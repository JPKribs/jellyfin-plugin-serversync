#pragma warning disable CA2100 // SQL is internal and parameterized.
using System;
using System.Security.Cryptography;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Peer;

/// <summary>
/// Holds the pairing secrets that tie a peer's requests to the server entry they claim to come from.
/// Every key a peer holds for this server is an administrator's key, so the key alone cannot say which
/// peer is calling. A pairing secret can: this server issues one to a peer over its own connection to
/// that peer, and the peer presents it on every request that names it as the sender.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class PeerPairingStore : QueueStoreBase
{
    private readonly SecretProtector _secrets;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _rechecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.SemaphoreSlim> _pairing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The lock that keeps pairings with one entry from running at once. Two at once would each mint a
    /// secret, and the two sides could keep different ones.
    /// </summary>
    /// <param name="peerKey">The server entry's key.</param>
    /// <returns>The entry's lock.</returns>
    public System.Threading.SemaphoreSlim PairingLock(string peerKey) => _pairing.GetOrAdd(peerKey, _ => new System.Threading.SemaphoreSlim(1, 1));

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerPairingStore"/> class.
    /// </summary>
    /// <param name="databaseProvider">Database provider.</param>
    /// <param name="secrets">Protects the secrets at rest.</param>
    /// <param name="logger">Logger.</param>
    public PeerPairingStore(ISyncDatabaseProvider databaseProvider, SecretProtector secrets, ILogger<PeerPairingStore> logger)
        : base(databaseProvider, logger)
    {
        _secrets = secrets;
    }

    /// <summary>Makes a fresh secret.</summary>
    /// <returns>The secret, URL safe.</returns>
    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Whether a presented secret matches an expected one, compared in constant time.</summary>
    /// <param name="presented">What the request carried.</param>
    /// <param name="expected">What this server issued.</param>
    /// <returns>True when they match.</returns>
    public static bool Matches(string? presented, string? expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        var a = System.Text.Encoding.UTF8.GetBytes(presented);
        var b = System.Text.Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>The secret a peer must present when it names itself as the sender, or null when it was never issued one.</summary>
    /// <param name="peerKey">The server entry's key.</param>
    /// <returns>The secret.</returns>
    public string? GetInbound(string peerKey) => Unprotect(ReadColumn(peerKey, "InboundSecret"));

    /// <summary>The secret this server presents to a peer, or null when the peer never issued one.</summary>
    /// <param name="peerKey">The server entry's key.</param>
    /// <returns>The secret.</returns>
    public string? GetOutbound(string peerKey) => Unprotect(ReadColumn(peerKey, "OutboundSecret"));

    /// <summary>Stores the secret a peer must present to this server, and clears any note that it refused to pair.</summary>
    /// <param name="peerKey">The server entry's key.</param>
    /// <param name="secret">The secret.</param>
    public void SetInbound(string peerKey, string secret)
    {
        Upsert(peerKey, "InboundSecret", secret);
        Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE PeerPairings SET InboundRefusedAt = NULL WHERE PeerKey = @peer";
            Add(cmd, "@peer", peerKey);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Notes that the peer refused this server's key when it tried to issue a secret, which happens when
    /// the key is a standard user's. Such a peer cannot be issued a secret, so its requests are taken on
    /// its word, as every request was before pairing existed.
    /// </summary>
    /// <param name="peerKey">The server entry's key.</param>
    public void MarkInboundRefused(string peerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerKey);
        Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO PeerPairings (PeerKey, InboundSecret, InboundRefusedAt, UpdatedAt) VALUES (@peer, NULL, @at, @at)
                ON CONFLICT(PeerKey) DO UPDATE SET InboundSecret = NULL, InboundRefusedAt = @at, UpdatedAt = @at";
            Add(cmd, "@peer", peerKey);
            Add(cmd, "@at", Stamp(DateTime.UtcNow));
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Claims the right to check a refused pairing again, at most once per interval per peer, so a stream
    /// of requests from that peer starts one check rather than one each.
    /// </summary>
    /// <param name="peerKey">The server entry's key.</param>
    /// <param name="interval">The least time between checks.</param>
    /// <returns>True when the caller should check now.</returns>
    public bool TryClaimRecheck(string peerKey, TimeSpan interval)
    {
        var now = DateTime.UtcNow;
        var claimed = false;
        _rechecks.AddOrUpdate(
            peerKey,
            _ =>
            {
                claimed = true;
                return now;
            },
            (_, last) =>
            {
                if (now - last < interval)
                {
                    return last;
                }

                claimed = true;
                return now;
            });
        return claimed;
    }

    /// <summary>When the peer last refused this server's key, or null when it never did or has since paired.</summary>
    /// <param name="peerKey">The server entry's key.</param>
    /// <returns>The time, in UTC.</returns>
    public DateTime? InboundRefusedAt(string peerKey)
    {
        var stored = ReadColumn(peerKey, "InboundRefusedAt");
        return string.IsNullOrEmpty(stored) ? null : Unstamp(stored);
    }

    /// <summary>Stores the secret this server presents to a peer.</summary>
    /// <param name="peerKey">The server entry's key.</param>
    /// <param name="secret">The secret.</param>
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

    // A secret that can no longer be decrypted, after the protection key changed, reads as no secret, so
    // the next pairing issues a fresh one rather than sending an empty one forever.
    private string? Unprotect(string? stored)
    {
        var plain = string.IsNullOrEmpty(stored) ? null : _secrets.Unprotect(stored);
        return string.IsNullOrEmpty(plain) ? null : plain;
    }

    private string? ReadColumn(string peerKey, string column) => Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM PeerPairings WHERE PeerKey = @peer";
        Add(cmd, "@peer", peerKey);
        var value = cmd.ExecuteScalar();
        return value is string text ? text : null;
    });

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
