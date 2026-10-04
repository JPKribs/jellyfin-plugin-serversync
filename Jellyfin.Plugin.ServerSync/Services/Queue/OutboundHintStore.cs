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
    /// an edit made after delivery is delivered again. The hint id is this server's id and a random id.
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
                    HintId, PeerKey, Kind, Key, ItemPath, ItemId, ItemType, UserId, UserName,
                    VersionServerId, VersionTimestamp, Recorded, State, Attempts, NextAttempt, SentAt, LastError, CreatedAt
                ) VALUES (
                    @hint, @peer, @kind, @key, @itemPath, @itemId, @itemType, @userId, @userName,
                    @versionServer, @versionAt, @recorded, 0, 0, @now, NULL, NULL, @now
                )
                ON CONFLICT(PeerKey, Kind, Key) DO UPDATE SET
                    HintId = CASE WHEN HintId = '' THEN @hint ELSE HintId END,
                    VersionServerId = CASE WHEN @versionAt >= VersionTimestamp AND (@recorded = 1 OR Recorded = 0) THEN @versionServer ELSE VersionServerId END,
                    VersionTimestamp = CASE WHEN @versionAt >= VersionTimestamp AND (@recorded = 1 OR Recorded = 0) THEN @versionAt ELSE VersionTimestamp END,
                    Recorded = MAX(Recorded, @recorded),
                    ItemPath = @itemPath,
                    ItemId = @itemId,
                    ItemType = @itemType,
                    UserId = @userId,
                    UserName = @userName,
                    State = 0,
                    Attempts = 0,
                    NextAttempt = @now,
                    SentAt = NULL,
                    LastError = NULL
                RETURNING Id, HintId";
            // The id is minted before the insert, so the row and its id land in one statement and a crash
            // cannot leave a row with no id. It stays the same for every later edit of the object, and is
            // never reused after the database is reset, since a peer may still remember a completed one.
            Add(cmd, "@hint", originServerId + ":" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
            Add(cmd, "@peer", row.PeerKey);
            Add(cmd, "@kind", (int)row.Kind);
            Add(cmd, "@key", row.Key);
            Add(cmd, "@itemPath", row.ItemPath);
            Add(cmd, "@itemId", row.ItemId);
            Add(cmd, "@itemType", row.ItemType);
            Add(cmd, "@userId", row.UserId);
            Add(cmd, "@userName", row.UserName);
            Add(cmd, "@versionServer", row.VersionServerId);
            Add(cmd, "@versionAt", Stamp(row.VersionTimestamp));
            Add(cmd, "@recorded", row.Recorded ? 1 : 0);
            Add(cmd, "@now", Stamp(DateTime.UtcNow));
            using var reader = cmd.ExecuteReader();
            reader.Read();
            row.Id = reader.GetInt64(0);
            row.HintId = reader.GetString(1);
        });
    }

    /// <summary>
    /// Keeps at most the cap of pending rows for one peer, dropping the oldest. A paused peer would
    /// otherwise accumulate rows without bound. The scheduled tasks carry whatever they would have. Run
    /// once per delivery pass rather than on every insert, so a scan raising thousands of hints does not
    /// count the table thousands of times.
    /// </summary>
    /// <param name="peerKey">The peer's entry key.</param>
    /// <returns>How many rows were dropped.</returns>
    public int TrimPending(string peerKey)
    {
        var trimmed = 0;
        Write(conn =>
        {
            using var count = conn.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM OutboundHints WHERE PeerKey = @peer AND State = 0";
            Add(count, "@peer", peerKey);
            if (Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) <= HintProtocol.MaxPendingPerPeer)
            {
                return;
            }

            using var trim = conn.CreateCommand();
            trim.CommandText = @"
                DELETE FROM OutboundHints
                WHERE PeerKey = @peer AND State = 0 AND Id NOT IN (
                    SELECT Id FROM OutboundHints WHERE PeerKey = @peer AND State = 0 ORDER BY VersionTimestamp DESC, Id DESC LIMIT @cap)";
            Add(trim, "@peer", peerKey);
            Add(trim, "@cap", HintProtocol.MaxPendingPerPeer);
            trimmed = trim.ExecuteNonQuery();
        });
        if (trimmed > 0)
        {
            Logger.LogWarning("Dropped {Count} of the oldest pending hint(s) for peer {Peer}: more than {Cap} were waiting", trimmed, peerKey, HintProtocol.MaxPendingPerPeer);
        }

        return trimmed;
    }

    /// <summary>
    /// Removes rows that have sat sent or failed for longer than the cutoff. A sent row a peer never
    /// completes, and a failed row nobody discards, would otherwise stay forever. The scheduled tasks
    /// carry whatever they would have.
    /// </summary>
    /// <param name="before">The cutoff.</param>
    /// <returns>How many rows were removed.</returns>
    public int ExpireSettled(DateTime before)
    {
        var removed = 0;
        Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM OutboundHints WHERE State <> 0 AND COALESCE(SentAt, CreatedAt) < @before";
            Add(cmd, "@before", Stamp(before));
            removed = cmd.ExecuteNonQuery();
        });
        return removed;
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

    // Every state change below names the version that was sent. An edit that merges into the row while
    // the request is in flight moves the version on, and the change then leaves the row pending so the
    // newer edit is sent too, rather than marking it sent, failed, or done on the strength of an older one.

    /// <summary>Marks rows accepted by the peer, each only while it still carries the version sent.</summary>
    /// <param name="rows">The rows as sent.</param>
    /// <param name="utcNow">Now.</param>
    public void MarkSent(IEnumerable<OutboundHint> rows, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = rows.ToList();
        if (list.Count == 0)
        {
            return;
        }

        Write(conn =>
        {
            using var transaction = conn.BeginTransaction();
            foreach (var row in list)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE OutboundHints SET State = 1, SentAt = @now, LastError = NULL WHERE Id = @id AND State = 0 AND VersionTimestamp = @version";
                Add(cmd, "@now", Stamp(utcNow));
                Add(cmd, "@id", row.Id);
                Add(cmd, "@version", Stamp(row.VersionTimestamp));
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        });
    }

    /// <summary>Marks a row the peer rejected for good, while it still carries the version sent.</summary>
    /// <param name="row">The row as sent.</param>
    /// <param name="reason">Why.</param>
    public void MarkFailed(OutboundHint row, string reason)
    {
        ArgumentNullException.ThrowIfNull(row);
        Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE OutboundHints SET State = 2, LastError = @reason WHERE Id = @id AND VersionTimestamp = @version";
            Add(cmd, "@reason", reason);
            Add(cmd, "@id", row.Id);
            Add(cmd, "@version", Stamp(row.VersionTimestamp));
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Puts rows off until later, each only while it still carries the version sent.</summary>
    /// <param name="rows">The rows as sent.</param>
    /// <param name="nextAttempt">When to try again.</param>
    /// <param name="error">Why.</param>
    public void Defer(IEnumerable<OutboundHint> rows, DateTime nextAttempt, string error)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = rows.ToList();
        Write(conn =>
        {
            using var transaction = conn.BeginTransaction();
            foreach (var row in list)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE OutboundHints SET Attempts = Attempts + 1, NextAttempt = @next, LastError = @error WHERE Id = @id AND State = 0 AND VersionTimestamp = @version";
                Add(cmd, "@next", Stamp(nextAttempt));
                Add(cmd, "@error", error);
                Add(cmd, "@id", row.Id);
                Add(cmd, "@version", Stamp(row.VersionTimestamp));
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        });
    }

    /// <summary>Removes a row the peer declined, while it still carries the version sent.</summary>
    /// <param name="row">The row as sent.</param>
    /// <returns>True when it was removed.</returns>
    public bool DeleteSent(OutboundHint row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM OutboundHints WHERE Id = @id AND VersionTimestamp = @version";
            Add(cmd, "@id", row.Id);
            Add(cmd, "@version", Stamp(row.VersionTimestamp));
            return cmd.ExecuteNonQuery() > 0;
        });
    }

    /// <summary>
    /// Removes rows the peer reports done. A row is only removed when it still carries the version the
    /// peer applied. A row that was edited again after delivery stays pending with its newer version.
    /// </summary>
    /// <param name="peerKey">The entry key of the peer that reports them, whose rows alone may be removed.</param>
    /// <param name="completed">The peer's report.</param>
    /// <returns>How many rows were removed.</returns>
    public int Complete(string peerKey, IEnumerable<CompletedHint> completed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerKey);
        ArgumentNullException.ThrowIfNull(completed);
        var removed = 0;
        var now = DateTime.UtcNow;
        Write(conn =>
        {
            using var transaction = conn.BeginTransaction();
            foreach (var done in completed)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM OutboundHints WHERE PeerKey = @peer AND HintId = @hint AND VersionTimestamp <= @version";
                Add(cmd, "@peer", peerKey);
                Add(cmd, "@hint", done.HintId);
                Add(cmd, "@version", Stamp(HintProtocol.BoundVersion(done.VersionTimestamp, now)));
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
    public int DeleteForPeer(string peerKey) => Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM OutboundHints WHERE PeerKey = @peer";
        Add(cmd, "@peer", peerKey);
        return cmd.ExecuteNonQuery();
    });

    /// <summary>Removes one row, used by the operator to discard a failed hint.</summary>
    /// <param name="id">The row id.</param>
    /// <returns><c>true</c> when a row was removed.</returns>
    public bool Delete(long id) => Write(conn =>
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
        ItemType = Text(reader, "ItemType"),
        ItemId = Text(reader, "ItemId"),
        UserId = Text(reader, "UserId"),
        UserName = Text(reader, "UserName"),
        VersionServerId = reader.GetString(reader.GetOrdinal("VersionServerId")),
        VersionTimestamp = Unstamp(reader.GetString(reader.GetOrdinal("VersionTimestamp"))),
        Recorded = reader.GetInt32(reader.GetOrdinal("Recorded")) != 0,
        State = (OutboundState)reader.GetInt32(reader.GetOrdinal("State")),
        Attempts = reader.GetInt32(reader.GetOrdinal("Attempts")),
        NextAttempt = Unstamp(reader.GetString(reader.GetOrdinal("NextAttempt"))),
        SentAt = Time(reader, "SentAt"),
        LastError = Text(reader, "LastError"),
        CreatedAt = Unstamp(reader.GetString(reader.GetOrdinal("CreatedAt")))
    };
}
