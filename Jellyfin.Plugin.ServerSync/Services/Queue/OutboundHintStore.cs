#pragma warning disable CA2100 // SQL is internal and parameterized.
using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// The hints this server still owes its peers. See <see cref="OutboundHint"/> for the row's life.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class OutboundHintStore : QueueStoreBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OutboundHintStore"/> class.
    /// </summary>
    /// <param name="databaseProvider">The database provider.</param>
    /// <param name="logger">Logger.</param>
    public OutboundHintStore(ISyncDatabaseProvider databaseProvider, ILogger<OutboundHintStore> logger)
        : base(databaseProvider, logger)
    {
    }

    /// <summary>
    /// Records a hint for one peer. An existing row for the same peer, kind, and key is reused: it goes
    /// back to pending with the newer version and a fresh attempt count, whatever state it was in, so
    /// an edit made after delivery is delivered again. The hint id is this server's id and the row id.
    /// </summary>
    /// <param name="row">The row to store. Its id and hint id are filled in.</param>
    /// <param name="originServerId">This server's id.</param>
    public void Enqueue(OutboundHint row, string originServerId)
    {
        ArgumentNullException.ThrowIfNull(row);
        Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO OutboundHints (
                    HintId, PeerKey, Kind, Key, ItemPath, ItemId, UserId, UserName,
                    VersionServerId, VersionTimestamp, State, Attempts, NextAttempt, SentAt, LastError, CreatedAt
                ) VALUES (
                    '', @peer, @kind, @key, @itemPath, @itemId, @userId, @userName,
                    @versionServer, @versionAt, 0, 0, @now, NULL, NULL, @now
                )
                ON CONFLICT(PeerKey, Kind, Key) DO UPDATE SET
                    ItemPath = @itemPath,
                    ItemId = @itemId,
                    UserId = @userId,
                    UserName = @userName,
                    VersionServerId = CASE WHEN @versionAt >= VersionTimestamp THEN @versionServer ELSE VersionServerId END,
                    VersionTimestamp = CASE WHEN @versionAt >= VersionTimestamp THEN @versionAt ELSE VersionTimestamp END,
                    State = 0,
                    Attempts = 0,
                    NextAttempt = @now,
                    SentAt = NULL,
                    LastError = NULL
                RETURNING Id";
            Add(cmd, "@peer", row.PeerKey);
            Add(cmd, "@kind", (int)row.Kind);
            Add(cmd, "@key", row.Key);
            Add(cmd, "@itemPath", row.ItemPath);
            Add(cmd, "@itemId", row.ItemId);
            Add(cmd, "@userId", row.UserId);
            Add(cmd, "@userName", row.UserName);
            Add(cmd, "@versionServer", row.VersionServerId);
            Add(cmd, "@versionAt", Stamp(row.VersionTimestamp));
            Add(cmd, "@now", Stamp(DateTime.UtcNow));
            row.Id = Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            row.HintId = $"{originServerId}:{row.Id}";

            using var stamp = conn.CreateCommand();
            stamp.CommandText = "UPDATE OutboundHints SET HintId = @hint WHERE Id = @id";
            Add(stamp, "@hint", row.HintId);
            Add(stamp, "@id", row.Id);
            stamp.ExecuteNonQuery();

            // A paused peer would otherwise accumulate rows without bound. The oldest pending rows beyond
            // the cap go; the scheduled tasks carry whatever they would have.
            using var count = conn.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM OutboundHints WHERE PeerKey = @peer AND State = 0";
            Add(count, "@peer", row.PeerKey);
            if (Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) <= HintProtocol.MaxPendingPerPeer)
            {
                return;
            }

            using var trim = conn.CreateCommand();
            trim.CommandText = @"
                DELETE FROM OutboundHints
                WHERE PeerKey = @peer AND State = 0 AND Id NOT IN (
                    SELECT Id FROM OutboundHints WHERE PeerKey = @peer AND State = 0 ORDER BY Id DESC LIMIT @cap)";
            Add(trim, "@peer", row.PeerKey);
            Add(trim, "@cap", HintProtocol.MaxPendingPerPeer);
            var trimmed = trim.ExecuteNonQuery();
            if (trimmed > 0)
            {
                Logger.LogWarning("Dropped {Count} of the oldest pending hint(s) for peer {Peer}: more than {Cap} were waiting", trimmed, row.PeerKey, HintProtocol.MaxPendingPerPeer);
            }
        });
    }

    /// <summary>Returns the pending rows for one peer that are due, oldest first.</summary>
    /// <param name="peerKey">The peer's entry key.</param>
    /// <param name="utcNow">Now.</param>
    /// <param name="limit">The most rows to return.</param>
    /// <returns>The rows.</returns>
    public IList<OutboundHint> GetDue(string peerKey, DateTime utcNow, int limit) => Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM OutboundHints WHERE PeerKey = @peer AND State = 0 AND NextAttempt <= @now ORDER BY Id LIMIT @limit";
        Add(cmd, "@peer", peerKey);
        Add(cmd, "@now", Stamp(utcNow));
        Add(cmd, "@limit", limit);
        return ReadAll(cmd);
    });

    /// <summary>Returns the entry keys of every peer that has rows, in any state.</summary>
    /// <returns>The peer keys.</returns>
    public IList<string> GetPeerKeys() => Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT PeerKey FROM OutboundHints";
        using var reader = cmd.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read())
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    });

    /// <summary>Returns every row, newest first, for the operator.</summary>
    /// <returns>The rows.</returns>
    public IList<OutboundHint> GetAll() => GetRecent(int.MaxValue);

    /// <summary>Returns the newest rows, for a view that polls often and must not carry the whole table.</summary>
    /// <param name="limit">The most rows to return.</param>
    /// <returns>The rows, newest first.</returns>
    public IList<OutboundHint> GetRecent(int limit) => Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM OutboundHints ORDER BY Id DESC LIMIT @limit";
        Add(cmd, "@limit", limit);
        return ReadAll(cmd);
    });

    /// <summary>Marks rows accepted by the peer.</summary>
    /// <param name="ids">The row ids.</param>
    /// <param name="utcNow">Now.</param>
    public void MarkSent(IEnumerable<long> ids, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var list = ids.ToList();
        if (list.Count == 0)
        {
            return;
        }

        Write(conn =>
        {
            using var transaction = conn.BeginTransaction();
            foreach (var id in list)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE OutboundHints SET State = 1, SentAt = @now, LastError = NULL WHERE Id = @id AND State = 0";
                Add(cmd, "@now", Stamp(utcNow));
                Add(cmd, "@id", id);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        });
    }

    /// <summary>Marks a row permanently failed, with the peer's reason.</summary>
    /// <param name="id">The row id.</param>
    /// <param name="reason">Why.</param>
    public void MarkFailed(long id, string reason) => Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE OutboundHints SET State = 2, LastError = @reason WHERE Id = @id";
        Add(cmd, "@reason", reason);
        Add(cmd, "@id", id);
        cmd.ExecuteNonQuery();
    });

    /// <summary>Schedules rows for another attempt after a failed delivery.</summary>
    /// <param name="ids">The row ids.</param>
    /// <param name="nextAttempt">When to try again.</param>
    /// <param name="error">What went wrong.</param>
    public void Defer(IEnumerable<long> ids, DateTime nextAttempt, string error)
    {
        ArgumentNullException.ThrowIfNull(ids);
        Write(conn =>
        {
            using var transaction = conn.BeginTransaction();
            foreach (var id in ids)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE OutboundHints SET Attempts = Attempts + 1, NextAttempt = @next, LastError = @error WHERE Id = @id AND State = 0";
                Add(cmd, "@next", Stamp(nextAttempt));
                Add(cmd, "@error", error);
                Add(cmd, "@id", id);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        });
    }

    /// <summary>
    /// Removes rows the peer reports done. A row is only removed when it still carries the version the
    /// peer applied. A row that was edited again after delivery stays pending with its newer version.
    /// </summary>
    /// <param name="completed">The peer's report.</param>
    /// <returns>How many rows were removed.</returns>
    public int Complete(IEnumerable<CompletedHint> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        var removed = 0;
        Write(conn =>
        {
            using var transaction = conn.BeginTransaction();
            foreach (var done in completed)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM OutboundHints WHERE HintId = @hint AND VersionTimestamp <= @version";
                Add(cmd, "@hint", done.HintId);
                Add(cmd, "@version", Stamp(done.VersionTimestamp));
                removed += cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        });
        return removed;
    }

    /// <summary>Returns the sent rows for a peer that have waited longer than the grace period.</summary>
    /// <param name="peerKey">The peer's entry key.</param>
    /// <param name="sentBefore">Rows sent before this time are returned.</param>
    /// <returns>The rows.</returns>
    public IList<OutboundHint> GetSentBefore(string peerKey, DateTime sentBefore) => Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM OutboundHints WHERE PeerKey = @peer AND State = 1 AND SentAt <= @before ORDER BY Id";
        Add(cmd, "@peer", peerKey);
        Add(cmd, "@before", Stamp(sentBefore));
        return ReadAll(cmd);
    });

    /// <summary>Puts sent rows back to pending because the peer no longer holds them and never reported them done.</summary>
    /// <param name="ids">The row ids.</param>
    /// <param name="utcNow">Now.</param>
    public void Resend(IEnumerable<long> ids, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(ids);
        Write(conn =>
        {
            using var transaction = conn.BeginTransaction();
            foreach (var id in ids)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE OutboundHints SET State = 0, Attempts = 0, NextAttempt = @now, SentAt = NULL, LastError = 'peer lost the hint before completing it' WHERE Id = @id AND State = 1";
                Add(cmd, "@now", Stamp(utcNow));
                Add(cmd, "@id", id);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        });
    }

    /// <summary>Removes every row for a peer, used when the peer leaves the configuration.</summary>
    /// <param name="peerKey">The peer's entry key.</param>
    /// <returns>How many rows were removed.</returns>
    public int DeleteForPeer(string peerKey) => Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM OutboundHints WHERE PeerKey = @peer";
        Add(cmd, "@peer", peerKey);
        return cmd.ExecuteNonQuery();
    });

    /// <summary>Removes one row, used by the operator to discard a failed hint.</summary>
    /// <param name="id">The row id.</param>
    /// <returns><c>true</c> when a row was removed.</returns>
    public bool Delete(long id) => Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM OutboundHints WHERE Id = @id";
        Add(cmd, "@id", id);
        return cmd.ExecuteNonQuery() > 0;
    });

    /// <summary>Counts rows per state for the dashboard.</summary>
    /// <returns>Counts by state.</returns>
    public Dictionary<OutboundState, int> CountByState() => Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT State, COUNT(*) FROM OutboundHints GROUP BY State";
        using var reader = cmd.ExecuteReader();
        var counts = new Dictionary<OutboundState, int>();
        while (reader.Read())
        {
            counts[(OutboundState)reader.GetInt32(0)] = reader.GetInt32(1);
        }

        return counts;
    });

    private static List<OutboundHint> ReadAll(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var rows = new List<OutboundHint>();
        while (reader.Read())
        {
            rows.Add(Map(reader));
        }

        return rows;
    }

    private static OutboundHint Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("Id")),
        HintId = reader.GetString(reader.GetOrdinal("HintId")),
        PeerKey = reader.GetString(reader.GetOrdinal("PeerKey")),
        Kind = (HintKind)reader.GetInt32(reader.GetOrdinal("Kind")),
        Key = reader.GetString(reader.GetOrdinal("Key")),
        ItemPath = Text(reader, "ItemPath"),
        ItemId = Text(reader, "ItemId"),
        UserId = Text(reader, "UserId"),
        UserName = Text(reader, "UserName"),
        VersionServerId = reader.GetString(reader.GetOrdinal("VersionServerId")),
        VersionTimestamp = Unstamp(reader.GetString(reader.GetOrdinal("VersionTimestamp"))),
        State = (OutboundState)reader.GetInt32(reader.GetOrdinal("State")),
        Attempts = reader.GetInt32(reader.GetOrdinal("Attempts")),
        NextAttempt = Unstamp(reader.GetString(reader.GetOrdinal("NextAttempt"))),
        SentAt = Time(reader, "SentAt"),
        LastError = Text(reader, "LastError"),
        CreatedAt = Unstamp(reader.GetString(reader.GetOrdinal("CreatedAt")))
    };
}
