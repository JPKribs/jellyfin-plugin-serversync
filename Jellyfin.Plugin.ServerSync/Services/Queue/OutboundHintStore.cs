#pragma warning disable CA2100 // SQL is internal and parameterized.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using JPKribs.Jellyfin.Base;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Stores the hints this server still owes its peers.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class OutboundHintStore : QueueStoreBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OutboundHintStore"/> class.
    /// </summary>
    /// <param name="databaseProvider">Opens the database the hints are stored in.</param>
    /// <param name="logger">Writes rows dropped past the cap.</param>
    public OutboundHintStore(ISyncDatabaseProvider databaseProvider, ILogger<OutboundHintStore> logger)
        : base(databaseProvider, logger)
    {
    }

    /// <summary>
    /// Records a hint for one peer, putting a row for the same peer, kind, and key back to pending with the newer stamp.
    /// The hint id is minted with the insert, kept for every later edit of the object, and never reused, since a peer may remember it.
    /// </summary>
    /// <param name="row">The row to store. Its id and hint id are filled in.</param>
    /// <param name="originServerId">This server's id, which starts the hint id.</param>
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
            Add(cmd, "@hint", originServerId + ":" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Add(cmd, "@peer", row.PeerKey);
            Add(cmd, "@kind", (int)row.Kind);
            Add(cmd, "@key", row.Key);
            Add(cmd, "@itemPath", row.ItemPath);
            Add(cmd, "@itemId", row.ItemId);
            Add(cmd, "@itemType", WithWaitingParts(conn, row));
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
    /// Keeps at most the cap of pending rows for one peer, dropping the oldest, so a paused peer cannot gather rows without bound.
    /// The scheduled tasks carry whatever the dropped rows would have.
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
            if (Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) <= HintProtocol.MaxPendingPerPeer)
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
            Logger.LogWarning("Dropped {Count} of the oldest pending hint(s) for peer {Peer}: more than {Cap} were waiting", trimmed, FormatUtilities.SanitizeForLog(peerKey), HintProtocol.MaxPendingPerPeer);
        }

        return trimmed;
    }

    /// <summary>
    /// Removes rows that have sat sent or failed since before the cutoff, which would otherwise stay forever.
    /// The scheduled tasks carry whatever they would have.
    /// </summary>
    /// <param name="before">Rows sent or created before this are removed.</param>
    /// <returns>How many rows were removed.</returns>
    public int ExpireSettled(DateTime before) => Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM OutboundHints WHERE State <> 0 AND COALESCE(SentAt, CreatedAt) < @before";
        Add(cmd, "@before", Stamp(before));
        return cmd.ExecuteNonQuery();
    });

    /// <summary>Returns the pending rows for one peer that are due, oldest first.</summary>
    /// <param name="peerKey">The peer's entry key.</param>
    /// <param name="utcNow">Rows due at or before this time are returned.</param>
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

    /// <summary>
    /// Marks rows accepted by the peer, each only while it still carries the stamp sent.
    /// An edit that merged into a row while the request was in flight leaves it pending, so the newer edit is sent too.
    /// </summary>
    /// <param name="rows">The rows as sent.</param>
    /// <param name="utcNow">The time the rows were sent.</param>
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

    /// <summary>Marks a row the peer rejected for good, while it still carries the stamp sent.</summary>
    /// <param name="row">The row as sent.</param>
    /// <param name="reason">Why the peer rejected it.</param>
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

    /// <summary>Puts rows off until later, each only while it still carries the stamp sent.</summary>
    /// <param name="rows">The rows as sent.</param>
    /// <param name="nextAttempt">When to try again.</param>
    /// <param name="error">Why the delivery did not go through.</param>
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

    /// <summary>Removes a row the peer declined, while it still carries the stamp sent.</summary>
    /// <param name="row">The row as sent.</param>
    /// <returns><c>true</c> when it was removed.</returns>
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
    /// Removes rows the peer reports done, each only while it still carries the stamp the peer applied.
    /// A row that was edited again after delivery stays pending with its newer stamp.
    /// </summary>
    /// <param name="peerKey">The entry key of the peer that reports them, whose rows alone may be removed.</param>
    /// <param name="completed">The peer's report.</param>
    /// <returns>How many rows were removed.</returns>
    public int Complete(string peerKey, IEnumerable<CompletedHint> completed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerKey);
        ArgumentNullException.ThrowIfNull(completed);
        var now = DateTime.UtcNow;
        return Write(conn =>
        {
            var removed = 0;
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
            return removed;
        });
    }

    /// <summary>Returns the rows for one peer that carry the given hint ids.</summary>
    /// <param name="peerKey">The peer's entry key.</param>
    /// <param name="hintIds">The hint ids to look up.</param>
    /// <returns>The rows found, by hint id.</returns>
    public Dictionary<string, OutboundHint> GetByHintIds(string peerKey, IEnumerable<string> hintIds)
    {
        ArgumentNullException.ThrowIfNull(hintIds);
        var wanted = new HashSet<string>(hintIds, StringComparer.Ordinal);
        var found = new Dictionary<string, OutboundHint>(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return found;
        }

        return Read(conn =>
        {
            foreach (var hintId in wanted)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT * FROM OutboundHints WHERE PeerKey = @peer AND HintId = @hint";
                Add(cmd, "@peer", peerKey);
                Add(cmd, "@hint", hintId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    found[hintId] = Map(reader);
                }
            }

            return found;
        });
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
    /// <param name="ids">The ids of the rows to send again.</param>
    /// <param name="utcNow">The time the rows become due.</param>
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

    /// <summary>Makes every waiting row for a peer due now, used when the reason it was paused may be gone.</summary>
    /// <param name="peerKey">The peer's entry key.</param>
    /// <param name="utcNow">The time the rows become due.</param>
    /// <returns>How many rows were brought forward.</returns>
    public int BringForward(string peerKey, DateTime utcNow) => Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE OutboundHints SET NextAttempt = @now WHERE PeerKey = @peer AND State = 0 AND NextAttempt > @now";
        Add(cmd, "@now", Stamp(utcNow));
        Add(cmd, "@peer", peerKey);
        return cmd.ExecuteNonQuery();
    });

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
    /// <param name="id">The id of the row to remove.</param>
    /// <returns><c>true</c> when a row was removed.</returns>
    public bool Delete(long id) => Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM OutboundHints WHERE Id = @id";
        Add(cmd, "@id", id);
        return cmd.ExecuteNonQuery() > 0;
    });

    /// <summary>
    /// Puts every failed row back to pending with its attempts cleared, for the operator once a peer is back.
    /// </summary>
    /// <param name="utcNow">The time the rows become due.</param>
    /// <returns>How many rows were put back.</returns>
    public int RetryFailed(DateTime utcNow) => Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE OutboundHints SET State = 0, Attempts = 0, NextAttempt = @now, SentAt = NULL WHERE State = 2";
        Add(cmd, "@now", Stamp(utcNow));
        return cmd.ExecuteNonQuery();
    });

    /// <summary>Removes every failed row, used by the operator to discard them all at once.</summary>
    /// <returns>How many rows were removed.</returns>
    public int DeleteFailed() => Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM OutboundHints WHERE State = 2";
        return cmd.ExecuteNonQuery();
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

    /// <summary>Runs a query and maps every row it returns.</summary>
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

    /// <summary>Maps one database row to a hint.</summary>
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

    /// <summary>
    /// Adds the parts of a history hint still waiting for the same object to the hint that replaces it, so the earlier change is still taken.
    /// </summary>
    private static string? WithWaitingParts(SqliteConnection conn, OutboundHint row)
    {
        if (row.Kind != HintKind.History)
        {
            return row.ItemType;
        }

        var type = HintProtocol.SplitHistoryItemType(row.ItemType, out var parts);
        if (parts == HistoryParts.None)
        {
            return row.ItemType;
        }

        using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT ItemType FROM OutboundHints WHERE PeerKey = @owner AND Kind = @kind AND Key = @key AND State = 0";
        Add(probe, "@owner", row.PeerKey);
        Add(probe, "@kind", (int)row.Kind);
        Add(probe, "@key", row.Key);
        if (probe.ExecuteScalar() is string waiting)
        {
            HintProtocol.SplitHistoryItemType(waiting, out var earlier);
            parts |= earlier;
        }

        return HintProtocol.HistoryItemType(type, parts);
    }
}
