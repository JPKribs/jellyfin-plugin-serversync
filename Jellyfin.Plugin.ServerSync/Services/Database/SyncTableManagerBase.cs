#pragma warning disable CA2100 // Table name is an internal abstract property, not user input.
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using Jellyfin.Plugin.ServerSync.Models.Common;
using JPKribs.Jellyfin.Base;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Database;

/// <summary>
/// Implements the operations every sync table shares, which depend only on the standard status columns.
/// A subclass supplies the key lookup, the upsert, and the search for its own table.
/// </summary>
/// <typeparam name="TRecord">The row type the sync table holds.</typeparam>
/// <typeparam name="TKey">The natural key that names one row.</typeparam>
public abstract class SyncTableManagerBase<TRecord, TKey> : ISyncTableManager<TRecord, TKey>
    where TRecord : SyncRecord
    where TKey : notnull
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteReadOnly = 8;
    private const int SqliteCorrupt = 11;
    private const int SqliteCantOpen = 14;

    private readonly ISyncDatabaseProvider _provider;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncTableManagerBase{TRecord, TKey}"/> class.
    /// </summary>
    /// <param name="databaseProvider">Opens the database whose connection and write lock back this sync table.</param>
    /// <param name="logger">Writes log entries.</param>
    protected SyncTableManagerBase(ISyncDatabaseProvider databaseProvider, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(databaseProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _provider = databaseProvider;
        Logger = logger;
    }

    /// <summary>
    /// Gets the database, opened on first use so one that cannot open fails the call that needs it and not startup.
    /// </summary>
    private SyncDatabase Database => _provider.Database;

    /// <summary>
    /// Gets the write connection, resolved on each use so a reopened connection is picked up.
    /// </summary>
    private SqliteConnection Connection => Database.Connection;

    /// <summary>
    /// Gets the shared write lock for serializing mutations.
    /// </summary>
    private object WriteLock => Database.WriteLock;

    /// <summary>
    /// Gets the logger for use by subclasses.
    /// </summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// Gets the SQL table name. Must be a constant identifier, never derived
    /// from user input.
    /// </summary>
    protected abstract string TableName { get; }

    /// <summary>
    /// Gets the natural-key column of a table keyed by one column, read by the default <see cref="UpdateStatusByKey"/>.
    /// A table with a composite key overrides that method, so it may return any value here.
    /// </summary>
    protected abstract string KeyColumn { get; }

    /// <summary>
    /// Gets the SQL that ranks statuses for a page's sort, as Errored, Queued, Pending, Ignored, then Synced.
    /// A sync type may override it, as Content does to put Pending first for approvals.
    /// </summary>
    protected virtual string StatusPriorityOrderBy => @"
        CASE Status
            WHEN 3 THEN 0
            WHEN 1 THEN 1
            WHEN 0 THEN 2
            WHEN 4 THEN 3
            WHEN 2 THEN 4
            ELSE 5
        END";

    /// <summary>
    /// Builds the query for one page of full rows, which takes <c>@take</c> and <c>@skip</c>.
    /// It sorts the ids alone and then reads only that page's rows, so a deep page never sorts every wide row.
    /// </summary>
    /// <param name="table">The table to page through.</param>
    /// <param name="whereClause">The filter, with its <c>WHERE</c>, or empty for every row.</param>
    /// <param name="orderBy">The sort, ending in the id so the order is total.</param>
    /// <returns>The SQL text.</returns>
    protected static string PageQuery(string table, string whereClause, string orderBy) => $@"
        SELECT * FROM {table}
        WHERE Id IN (SELECT Id FROM {table} {whereClause} ORDER BY {orderBy} LIMIT @take OFFSET @skip)
        ORDER BY {orderBy}";

    /// <summary>
    /// Maps the current row of <paramref name="reader"/> into a record, filling the standard
    /// <see cref="SyncRecord"/> fields and the table's own.
    /// </summary>
    /// <param name="reader">The reader, positioned on the row.</param>
    /// <returns>The record.</returns>
    protected abstract TRecord MapFromReader(IDataRecord reader);

    /// <inheritdoc />
    public abstract TRecord? GetByKey(TKey key);

    /// <inheritdoc />
    public abstract void DeleteByKey(TKey key);

    /// <inheritdoc />
    public abstract void Upsert(TRecord record);

    /// <inheritdoc />
    public abstract PagedResult<TRecord> Paginate(PaginationRequest request);

    /// <inheritdoc />
    public TRecord? GetById(long id) => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {TableName} WHERE Id = @Id LIMIT 1";
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? MapFromReader(reader) : null;
        },
        fallback: null);

    /// <inheritdoc />
    public IList<TRecord> GetByStatus(SyncStatus status, int? limit = null) => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = limit.HasValue
                ? $"SELECT * FROM {TableName} WHERE Status = @Status LIMIT @Limit"
                : $"SELECT * FROM {TableName} WHERE Status = @Status";
            cmd.Parameters.AddWithValue("@Status", (int)status);
            if (limit.HasValue)
            {
                cmd.Parameters.AddWithValue("@Limit", limit.Value);
            }

            return ReadAll(cmd);
        },
        fallback: (IList<TRecord>)Array.Empty<TRecord>());

    /// <inheritdoc />
    public IList<TRecord> GetAllStrict() => Database.RunRead(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {TableName}";
        return ReadAll(cmd);
    });

    /// <summary>
    /// Runs a read that throws when the database cannot be read, rather than answering as if it held
    /// nothing. For a caller that would delete on an empty answer.
    /// </summary>
    /// <typeparam name="T">What the read returns.</typeparam>
    /// <param name="read">The read to run on a read connection.</param>
    /// <returns>What it read.</returns>
    protected T ReadStrict<T>(Func<SqliteConnection, T> read) => Database.RunRead(read);

    /// <inheritdoc />
    public IList<TRecord> GetByStatusStrict(SyncStatus status) => Database.RunRead(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {TableName} WHERE Status = @Status";
        cmd.Parameters.AddWithValue("@Status", (int)status);
        return ReadAll(cmd);
    });

    /// <inheritdoc />
    public int Count() => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {TableName}";
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        },
        fallback: 0);

    /// <inheritdoc />
    public int CountByStatus(SyncStatus status) => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {TableName} WHERE Status = @Status";
            cmd.Parameters.AddWithValue("@Status", (int)status);
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        },
        fallback: 0);

    /// <summary>
    /// Returns a per-<see cref="SyncStatus"/> row count for the table. Statuses
    /// not present in the table are omitted from the result.
    /// </summary>
    /// <returns>The row count for each status that has rows.</returns>
    public Dictionary<SyncStatus, int> GetStatusCounts() => ExecuteRead(
        conn =>
        {
            var counts = new Dictionary<SyncStatus, int>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT Status, COUNT(*) FROM {TableName} GROUP BY Status";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                counts[(SyncStatus)reader.GetInt32(0)] = reader.GetInt32(1);
            }

            return counts;
        },
        fallback: new Dictionary<SyncStatus, int>());

    /// <inheritdoc />
    public void DeleteById(long id) => ExecuteWrite(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName} WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.ExecuteNonQuery();
    });

    /// <inheritdoc />
    public int DeleteByIds(IReadOnlyCollection<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var deleted = 0;
        ExecuteWrite(conn =>
        {
            using var transaction = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = $"DELETE FROM {TableName} WHERE Id = @Id";
            var idParam = cmd.Parameters.Add("@Id", SqliteType.Integer);
            foreach (var id in ids)
            {
                idParam.Value = id;
                deleted += cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        });
        return deleted;
    }

    /// <summary>
    /// Writes many records in one transaction, so a scan of a large library commits once per batch and not once per row.
    /// A batch that fails is rolled back and written one record at a time, so one bad record costs only itself.
    /// </summary>
    /// <param name="records">The records to write.</param>
    /// <param name="onFailure">Called with each record that could not be written.</param>
    /// <returns>How many were written.</returns>
    public int UpsertMany(IReadOnlyList<TRecord> records, Action<TRecord, Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(onFailure);
        if (records.Count == 0)
        {
            return 0;
        }

        lock (WriteLock)
        {
            var conn = Connection;
            Exec(conn, "BEGIN IMMEDIATE");
            try
            {
                foreach (var record in records)
                {
                    Upsert(record);
                }

                Exec(conn, "COMMIT");
                return records.Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogDebug(ex, "Batch of {Count} writes to {Table} failed, writing them one at a time", records.Count, TableName);
                try
                {
                    Exec(conn, "ROLLBACK");
                }
                catch (SqliteException rollbackEx)
                {
                    // SQLite rolls back by itself on some errors, which leaves nothing to roll back.
                    Logger.LogDebug(rollbackEx, "Rollback after a failed batch on {Table}", TableName);
                }
            }
            catch
            {
                TryRollback(conn);
                throw;
            }

            var written = 0;
            foreach (var record in records)
            {
                try
                {
                    Upsert(record);
                    written++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    onFailure(record, ex);
                }
            }

            return written;
        }
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void TryRollback(SqliteConnection connection)
    {
        try
        {
            Exec(connection, "ROLLBACK");
        }
        catch (SqliteException)
        {
            // SQLite already rolled back, which leaves nothing to roll back.
        }
    }

    /// <summary>Removes every row that came from one configured server, used when the entry is removed.</summary>
    /// <param name="serverKey">The entry key the rows carry.</param>
    /// <returns>How many rows were removed.</returns>
    public int DeleteByServerKey(string serverKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverKey);
        var deleted = 0;
        ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {TableName} WHERE ServerKey = @ServerKey";
            cmd.Parameters.AddWithValue("@ServerKey", serverKey);
            deleted = cmd.ExecuteNonQuery();
        });
        return deleted;
    }

    /// <inheritdoc />
    public int ResetTable()
    {
        var deleted = 0;
        ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {TableName}";
            deleted = cmd.ExecuteNonQuery();
        });
        return deleted;
    }

    /// <inheritdoc />
    public void UpdateStatus(long id, SyncStatus status, string? reason = null) => ExecuteWrite(conn =>
    {
        using var cmd = conn.CreateCommand();
        var clauses = BuildStatusTransitionClauses(status);
        cmd.CommandText = $"UPDATE {TableName} SET {string.Join(", ", clauses)} WHERE Id = @Id";
        AddStatusTransitionParameters(cmd, status, reason);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.ExecuteNonQuery();
    });

    /// <summary>
    /// Updates the status of many rows in one transaction, and reports how many were updated and which ids were not found.
    /// A row can be deleted between the request and the write, and the caller reports those ids to the user.
    /// </summary>
    /// <param name="ids">The primary-key ids to update.</param>
    /// <param name="status">The new status.</param>
    /// <param name="reason">The reason, for a status that carries one.</param>
    /// <returns>How many rows were updated, and the ids that were not found.</returns>
    public (int Updated, IReadOnlyList<long> NotFoundIds) BulkUpdateStatusWithDetails(
        IReadOnlyList<long> ids,
        SyncStatus status,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return (0, Array.Empty<long>());
        }

        var updated = 0;
        var notFound = new List<long>();
        ExecuteWrite(conn =>
        {
            using var transaction = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            var clauses = BuildStatusTransitionClauses(status);
            cmd.CommandText = $"UPDATE {TableName} SET {string.Join(", ", clauses)} WHERE Id = @Id";
            AddStatusTransitionParameters(cmd, status, reason);
            var idParam = cmd.Parameters.Add("@Id", SqliteType.Integer);

            foreach (var id in ids)
            {
                idParam.Value = id;
                var rows = cmd.ExecuteNonQuery();
                if (rows > 0)
                {
                    updated += rows;
                }
                else
                {
                    notFound.Add(id);
                }
            }

            transaction.Commit();
        });
        return (updated, notFound);
    }

    /// <summary>
    /// Queues every row in one status with a single statement and no row cap, so a retry reaches rows beyond the loaded page.
    /// The Queued transition resets RetryCount, so each row gets its full allowance again.
    /// </summary>
    /// <param name="fromStatus">The status whose rows are queued.</param>
    /// <returns>How many rows were queued.</returns>
    public int QueueAllWithStatus(SyncStatus fromStatus)
    {
        var updated = 0;
        ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            var clauses = BuildStatusTransitionClauses(SyncStatus.Queued);
            clauses.AddRange(OperatorQueueClauses);
            cmd.CommandText = $"UPDATE {TableName} SET {string.Join(", ", clauses)} WHERE Status = @fromStatus";
            AddStatusTransitionParameters(cmd, SyncStatus.Queued, reason: null);
            cmd.Parameters.AddWithValue("@fromStatus", (int)fromStatus);
            updated = cmd.ExecuteNonQuery();
        });
        return updated;
    }

    /// <summary>
    /// Gets extra SET clauses a sync type applies when an operator queues its rows, on top of the status transition.
    /// A sync type that skips categories it believes are already applied clears that belief here, so a queued row is applied again.
    /// </summary>
    protected virtual IReadOnlyList<string> OperatorQueueClauses => Array.Empty<string>();

    /// <inheritdoc />
    public virtual void UpdateStatusByKey(TKey key, SyncStatus status, string? reason = null) => ExecuteWrite(conn =>
    {
        using var cmd = conn.CreateCommand();
        var clauses = BuildStatusTransitionClauses(status);
        cmd.CommandText = $"UPDATE {TableName} SET {string.Join(", ", clauses)} WHERE {KeyColumn} = @key";
        AddStatusTransitionParameters(cmd, status, reason);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.ExecuteNonQuery();
    });

    /// <summary>
    /// Builds the SET clauses every status transition shares, which are Status, StatusDate, and Reason, plus LastSyncTime on
    /// <see cref="SyncStatus.Synced"/>. When <paramref name="trackRetryCount"/> is <c>true</c>, RetryCount resets on Synced and Queued and rises on Errored.
    /// </summary>
    /// <param name="status">The status the row moves to.</param>
    /// <param name="trackRetryCount">Whether the clauses also manage the RetryCount column.</param>
    /// <returns>The SET clause fragments, to be joined with commas.</returns>
    protected static List<string> BuildStatusTransitionClauses(SyncStatus status, bool trackRetryCount = true)
    {
        var clauses = new List<string>
        {
            "Status = @status",
            "StatusDate = @statusDate",
            "Reason = @reason"
        };

        if (status == SyncStatus.Synced)
        {
            clauses.Add("LastSyncTime = @statusDate");
            if (trackRetryCount)
            {
                clauses.Add("RetryCount = 0");
            }
        }
        else if (status == SyncStatus.Queued && trackRetryCount)
        {
            // An operator queued the row, so it gets its full retry allowance.
            clauses.Add("RetryCount = 0");
        }
        else if (status == SyncStatus.Errored && trackRetryCount)
        {
            clauses.Add("RetryCount = COALESCE(RetryCount, 0) + 1");
        }

        return clauses;
    }

    /// <summary>
    /// Binds <c>@status</c>, <c>@statusDate</c>, and <c>@reason</c> onto <paramref name="cmd"/>.
    /// Synced clears the reason, so an earlier error does not outlive a successful sync.
    /// </summary>
    /// <param name="cmd">The command to bind onto.</param>
    /// <param name="status">The new status.</param>
    /// <param name="reason">The reason, which is dropped for Synced.</param>
    protected static void AddStatusTransitionParameters(SqliteCommand cmd, SyncStatus status, string? reason)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        cmd.Parameters.AddWithValue("@status", (int)status);
        cmd.Parameters.AddWithValue("@statusDate", FormatTimestamp(DateTime.UtcNow));
        var effectiveReason = status == SyncStatus.Synced ? null : reason;
        cmd.Parameters.AddWithValue("@reason", (object?)effectiveReason ?? DBNull.Value);
    }

    /// <summary>
    /// Reads a nullable string column.
    /// </summary>
    /// <param name="reader">The reader, positioned on the row.</param>
    /// <param name="column">The column's name.</param>
    /// <returns>The value, or null when the column is null.</returns>
    protected static string? ReadNullableString(IDataRecord reader, string column)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var ord = reader.GetOrdinal(column);
        return reader.IsDBNull(ord) ? null : reader.GetString(ord);
    }

    /// <summary>
    /// Reads a nullable Int64 column.
    /// </summary>
    /// <param name="reader">The reader, positioned on the row.</param>
    /// <param name="column">The column's name.</param>
    /// <returns>The value, or null when the column is null.</returns>
    protected static long? ReadNullableInt64(IDataRecord reader, string column)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var ord = reader.GetOrdinal(column);
        return reader.IsDBNull(ord) ? null : reader.GetInt64(ord);
    }

    /// <summary>
    /// Reads a nullable Int32 column.
    /// </summary>
    /// <param name="reader">The reader, positioned on the row.</param>
    /// <param name="column">The column's name.</param>
    /// <returns>The value, or null when the column is null.</returns>
    protected static int? ReadNullableInt32(IDataRecord reader, string column)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var ord = reader.GetOrdinal(column);
        return reader.IsDBNull(ord) ? null : reader.GetInt32(ord);
    }

    /// <summary>
    /// Reads a non-null timestamp column stored as ISO-8601 round-trip text.
    /// </summary>
    /// <param name="reader">The reader, positioned on the row.</param>
    /// <param name="column">The column's name.</param>
    /// <returns>The value.</returns>
    protected static DateTime ReadDateTime(IDataRecord reader, string column)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return ParseTimestamp(reader.GetString(reader.GetOrdinal(column)));
    }

    /// <summary>
    /// Reads a nullable timestamp column stored as ISO-8601 round-trip text.
    /// </summary>
    /// <param name="reader">The reader, positioned on the row.</param>
    /// <param name="column">The column's name.</param>
    /// <returns>The value, or null when the column is null.</returns>
    protected static DateTime? ReadNullableDateTime(IDataRecord reader, string column)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var ord = reader.GetOrdinal(column);
        return reader.IsDBNull(ord) ? null : ParseTimestamp(reader.GetString(ord));
    }

    /// <summary>
    /// Adds a nullable parameter, mapping null to <see cref="DBNull.Value"/>.
    /// </summary>
    /// <param name="cmd">The command to bind onto.</param>
    /// <param name="name">The parameter's name.</param>
    /// <param name="value">The value to bind.</param>
    protected static void AddNullable(SqliteCommand cmd, string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    /// <summary>
    /// Adds a timestamp parameter as ISO-8601 round-trip text.
    /// </summary>
    /// <param name="cmd">The command to bind onto.</param>
    /// <param name="name">The parameter's name.</param>
    /// <param name="value">The value to bind.</param>
    protected static void AddTimestamp(SqliteCommand cmd, string name, DateTime value)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        cmd.Parameters.AddWithValue(name, FormatTimestamp(value));
    }

    /// <summary>
    /// Adds a nullable timestamp parameter as ISO-8601 round-trip text or DBNull.
    /// </summary>
    /// <param name="cmd">The command to bind onto.</param>
    /// <param name="name">The parameter's name.</param>
    /// <param name="value">The value to bind.</param>
    protected static void AddNullableTimestamp(SqliteCommand cmd, string name, DateTime? value)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        cmd.Parameters.AddWithValue(name, value.HasValue ? FormatTimestamp(value.Value) : (object)DBNull.Value);
    }

    /// <summary>
    /// Formats a <see cref="DateTime"/> as UTC round trip text, the format of every timestamp column in this database.
    /// </summary>
    /// <param name="value">The time to format.</param>
    /// <returns>The text.</returns>
    protected static string FormatTimestamp(DateTime value) => JPKribs.Jellyfin.Base.UtcTime.Format(value);

    /// <summary>
    /// Parses a timestamp written by <see cref="FormatTimestamp"/>.
    /// </summary>
    /// <param name="value">The text to parse.</param>
    /// <returns>The time, or <see cref="DateTime.MinValue"/> when the text does not parse.</returns>
    protected static DateTime ParseTimestamp(string value)
        => JPKribs.Jellyfin.Base.UtcTime.TryParse(value, out var dt) ? dt : DateTime.MinValue;

    /// <summary>
    /// Reads all rows from <paramref name="cmd"/> using <see cref="MapFromReader"/>.
    /// </summary>
    /// <param name="cmd">The command to run.</param>
    /// <returns>The records.</returns>
    protected IList<TRecord> ReadAll(SqliteCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        var results = new List<TRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(MapFromReader(reader));
        }

        return results;
    }

    /// <summary>
    /// Executes a read on a read connection, beside any write in progress, and returns
    /// <paramref name="fallback"/> when the database is busy, locked, read-only, corrupt, or cannot open.
    /// </summary>
    /// <typeparam name="T">The type the read returns.</typeparam>
    /// <param name="readOperation">The read to run.</param>
    /// <param name="fallback">What to return when the database cannot be read.</param>
    /// <param name="callerName">The calling member, for the log.</param>
    /// <returns>The read's result, or the fallback.</returns>
    protected T ExecuteRead<T>(
        Func<SqliteConnection, T> readOperation,
        T fallback,
        [CallerMemberName] string? callerName = null)
    {
        ArgumentNullException.ThrowIfNull(readOperation);
        try
        {
            return Database.RunRead(readOperation);
        }
        catch (SqliteException ex) when (
            ex.SqliteErrorCode == SqliteBusy ||
            ex.SqliteErrorCode == SqliteLocked ||
            ex.SqliteErrorCode == SqliteReadOnly ||
            ex.SqliteErrorCode == SqliteCantOpen)
        {
            Logger.LogWarning(ex, "Read '{Op}' on {Table} failed with SQLite error {Code}. Returning fallback", callerName, TableName, ex.SqliteErrorCode);
            return fallback;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteCorrupt)
        {
            Logger.LogError(ex, "Sync database is corrupt, so read '{Op}' on {Table} failed", callerName, TableName);
            return fallback;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("connection", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogWarning(ex, "Connection error during read '{Op}' on {Table}. Returning fallback", callerName, TableName);
            return fallback;
        }
    }

    /// <summary>
    /// Executes a write on the write connection under the write lock. Errors propagate, and the caller decides whether to retry.
    /// </summary>
    /// <param name="writeOperation">The write to run.</param>
    /// <param name="callerName">The calling member, for the log.</param>
    protected void ExecuteWrite(
        Action<SqliteConnection> writeOperation,
        [CallerMemberName] string? callerName = null)
    {
        ArgumentNullException.ThrowIfNull(writeOperation);
        try
        {
            lock (WriteLock)
            {
                writeOperation(Connection);
            }
        }
        catch (SqliteException ex)
        {
            Logger.LogDebug(ex, "Write '{Op}' on {Table} failed with SQLite error {Code}", callerName, TableName, ex.SqliteErrorCode);
            throw;
        }
    }
}
