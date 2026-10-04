#pragma warning disable CA2100 // SQL is internal and table names come from a fixed list.
using System;
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
        var updated = 0;
        Write(conn =>
        {
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
        });
        return updated;
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
        var requeued = 0;
        Write(conn =>
        {
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
        });
        return requeued;
    }
}
