#pragma warning disable CA2100 // SQL is internal and table names come from a fixed list.
using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>Bulk upkeep statements over the sync tables, for the maintenance service.</summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class MaintenanceStore : QueueStoreBase
{
    private static readonly string[] SyncTables = { "SyncItems", "HistorySyncItems", "UserSyncItems", "PeopleSyncItems", "MetadataSyncItems" };

    // Content keeps its own retry rules, since its applies download files and delete them.
    private static readonly string[] RetriedTables = { "HistorySyncItems", "UserSyncItems", "PeopleSyncItems", "MetadataSyncItems" };

    /// <summary>
    /// Initializes a new instance of the <see cref="MaintenanceStore"/> class.
    /// </summary>
    /// <param name="databaseProvider">Database provider.</param>
    /// <param name="logger">Logger.</param>
    public MaintenanceStore(ISyncDatabaseProvider databaseProvider, ILogger<MaintenanceStore> logger)
        : base(databaseProvider, logger)
    {
    }

    /// <summary>
    /// Gives rows written before servers became a list the key of the server they already resolve to,
    /// the first scan server, so removing that server removes them and no other server can claim them.
    /// </summary>
    /// <param name="serverKey">The first scan server's entry key.</param>
    /// <returns>How many rows were given a key.</returns>
    public int BackfillServerKey(string serverKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        return Write(conn =>
        {
            var updated = 0;
            using var transaction = conn.BeginTransaction();
            foreach (var table in SyncTables)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = $"UPDATE {table} SET ServerKey = @key WHERE ServerKey IS NULL OR ServerKey = ''";
                Add(cmd, "@key", serverKey);
                updated += cmd.ExecuteNonQuery();
            }

            transaction.Commit();
            return updated;
        });
    }

    private static readonly (string Table, string Label)[] WatchedTables =
    {
        ("SyncItems", "file"), ("HistorySyncItems", "watch history"), ("MetadataSyncItems", "metadata"), ("PeopleSyncItems", "person"), ("UserSyncItems", "user")
    };

    /// <summary>
    /// Notes every row that is failing now, keeps the time each first failed, and forgets the rows
    /// that have settled since. A row that is queued for another try is still failing as far as this
    /// goes: only a row that is no longer queued or errored has settled. Returns the rows that first
    /// failed before the cutoff and have not been reported, and marks them reported, so each failing
    /// row is raised once.
    /// </summary>
    /// <param name="now">The time to record for a row seen failing for the first time.</param>
    /// <param name="failingSince">Rows that first failed before this are due to be reported.</param>
    /// <returns>What to report, by kind. Empty when nothing is due.</returns>
    public IReadOnlyList<FailingRows> TrackFailures(DateTime now, DateTime failingSince)
    {
        return Write(conn =>
        {
            var due = new List<FailingRows>();
            using var transaction = conn.BeginTransaction();
            foreach (var (table, label) in WatchedTables)
            {
                using (var note = conn.CreateCommand())
                {
                    note.Transaction = transaction;
                    note.CommandText = $@"
                        INSERT OR IGNORE INTO SyncAttention (TableName, RowId, FirstFailed, Reason, Reported)
                            SELECT '{table}', Id, @now, Reason, 0 FROM {table} WHERE Status = 3 AND COALESCE(RetryCount, 0) < 1000000;
                        UPDATE SyncAttention SET Reason = (SELECT Reason FROM {table} WHERE {table}.Id = SyncAttention.RowId)
                            WHERE TableName = '{table}' AND RowId IN (SELECT Id FROM {table} WHERE Status = 3 AND Reason IS NOT NULL);
                        DELETE FROM SyncAttention WHERE TableName = '{table}' AND RowId NOT IN (SELECT Id FROM {table} WHERE Status IN (1, 3));";
                    Add(note, "@now", Stamp(now));
                    note.ExecuteNonQuery();
                }

                using var read = conn.CreateCommand();
                read.Transaction = transaction;
                read.CommandText = "SELECT COUNT(*), MAX(Reason) FROM SyncAttention WHERE TableName = @table AND Reported = 0 AND FirstFailed < @since";
                Add(read, "@table", table);
                Add(read, "@since", Stamp(failingSince));
                using (var reader = read.ExecuteReader())
                {
                    if (reader.Read() && reader.GetInt32(0) > 0)
                    {
                        due.Add(new FailingRows(label, reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
                    }
                }

                using var mark = conn.CreateCommand();
                mark.Transaction = transaction;
                mark.CommandText = "UPDATE SyncAttention SET Reported = 1 WHERE TableName = @table AND Reported = 0 AND FirstFailed < @since";
                Add(mark, "@table", table);
                Add(mark, "@since", Stamp(failingSince));
                mark.ExecuteNonQuery();
            }

            transaction.Commit();
            return (IReadOnlyList<FailingRows>)due;
        });
    }

    /// <summary>
    /// Gives errored rows that have sat for longer than the cutoff a fresh set of retries. A peer that
    /// restarted or a network that dropped burns every retry in minutes, and nothing would try the row
    /// again. A row that keeps failing now fails once a day rather than never being tried.
    /// </summary>
    /// <param name="erroredBefore">The cutoff.</param>
    /// <returns>How many rows were queued again.</returns>
    public int RequeueErrored(DateTime erroredBefore)
    {
        return Write(conn =>
        {
            var requeued = 0;
            using var transaction = conn.BeginTransaction();
            foreach (var table in RetriedTables)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = $"UPDATE {table} SET Status = 1, RetryCount = 0 WHERE Status = 3 AND StatusDate < @before";
                Add(cmd, "@before", Stamp(erroredBefore));
                requeued += cmd.ExecuteNonQuery();
            }

            transaction.Commit();
            return requeued;
        });
    }
}

/// <summary>What has been failing for long enough to tell the administrator about, for one kind of thing.</summary>
/// <param name="Label">What the rows are, such as "file".</param>
/// <param name="Count">How many rows.</param>
/// <param name="Reason">Why one of them failed, as an example.</param>
public sealed record FailingRows(string Label, int Count, string? Reason);
