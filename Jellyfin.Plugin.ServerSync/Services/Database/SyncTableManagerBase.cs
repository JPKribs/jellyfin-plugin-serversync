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

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Universal operations on a sync table — the parts that depend only on the
/// standard columns (<c>Id</c>, <c>Status</c>, <c>StatusDate</c>,
/// <c>LastSyncTime</c>, <c>Reason</c>). Schema-specific methods (key lookup,
/// upsert SQL, search) remain abstract for subclasses. Composite-key tables
/// override <see cref="UpdateStatusByKey"/> directly but should still use
/// <see cref="BuildStatusTransitionClauses"/> + <see cref="AddStatusTransitionParameters"/>
/// for consistent transition logic.
/// </summary>
/// <typeparam name="TRecord">Record type.</typeparam>
/// <typeparam name="TKey">Natural-key type.</typeparam>
public abstract class SyncTableManagerBase<TRecord, TKey> : ISyncTableManager<TRecord, TKey>
    where TRecord : SyncRecord
    where TKey : notnull
{
    private readonly SyncDatabase _database;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="database">Database whose connection and write-lock back this manager.</param>
    /// <param name="logger">Logger.</param>
    protected SyncTableManagerBase(SyncDatabase database, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        Logger = logger;
    }

    /// <summary>
    /// Gets the live SQLite connection (re-resolved per access — handles closes/reopens).
    /// </summary>
    private SqliteConnection Connection => _database.Connection;

    /// <summary>
    /// Gets the shared write lock for serializing mutations.
    /// </summary>
    private object WriteLock => _database.WriteLock;

    /// <summary>
    /// Gets the logger for use by subclasses.
    /// </summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// Gets the SQL table name. Must be a constant identifier — never derived
    /// from user input.
    /// </summary>
    protected abstract string TableName { get; }

    /// <summary>
    /// Gets the natural-key column name for single-column-keyed tables. Used
    /// by the default <see cref="UpdateStatusByKey"/> implementation.
    /// Composite-key tables can return any sentinel since they must override
    /// <see cref="UpdateStatusByKey"/> anyway.
    /// </summary>
    protected abstract string KeyColumn { get; }

    /// <summary>
    /// Gets the SQL fragment that defines the status priority for ORDER BY in
    /// pagination. Default ordering: Errored, Queued, Pending, Ignored,
    /// Synced. Override to customize per module (e.g. Content surfaces
    /// Pending first because the approval UX prioritizes new items).
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
    /// Maps the current row of <paramref name="reader"/> into a record. Must
    /// populate the standard <see cref="SyncRecord"/> fields plus the subclass
    /// payload.
    /// </summary>
    protected abstract TRecord MapFromReader(IDataRecord reader);

    /// <inheritdoc />
    public abstract TRecord? GetByKey(TKey key);

    /// <inheritdoc />
    public abstract void DeleteByKey(TKey key);

    /// <inheritdoc />
    public abstract void Upsert(TRecord record);

    /// <inheritdoc />
    public abstract PagedResult<TRecord> Paginate(PaginationRequest request);

    // ===================================================================
    // Universal CRUD on standard columns
    // ===================================================================

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
    public IList<TRecord> GetAll() => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {TableName}";
            return ReadAll(cmd);
        },
        fallback: (IList<TRecord>)Array.Empty<TRecord>());

    /// <inheritdoc />
    public int Count() => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {TableName}";
            var result = cmd.ExecuteScalar();
            return Convert.ToInt32(result, CultureInfo.InvariantCulture);
        },
        fallback: 0);

    /// <inheritdoc />
    public int CountByStatus(SyncStatus status) => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {TableName} WHERE Status = @Status";
            cmd.Parameters.AddWithValue("@Status", (int)status);
            var result = cmd.ExecuteScalar();
            return Convert.ToInt32(result, CultureInfo.InvariantCulture);
        },
        fallback: 0);

    /// <summary>
    /// Returns a per-<see cref="SyncStatus"/> row count for the table. Statuses
    /// not present in the table are omitted from the result.
    /// </summary>
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
    public int DeleteByStatus(SyncStatus status)
    {
        var deleted = 0;
        ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {TableName} WHERE Status = @Status";
            cmd.Parameters.AddWithValue("@Status", (int)status);
            deleted = cmd.ExecuteNonQuery();
        });
        return deleted;
    }

    /// <summary>
    /// Truncates the table — deletes every row regardless of status.
    /// Powers the per-module "Reset Database" admin actions.
    /// </summary>
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

    /// <inheritdoc />
    public int BulkUpdateStatus(IReadOnlyList<long> ids, SyncStatus status, string? reason = null)
    {
        var result = BulkUpdateStatusWithDetails(ids, status, reason);
        return result.Updated;
    }

    /// <summary>
    /// Same as <see cref="BulkUpdateStatus"/> but returns the per-call
    /// breakdown: how many rows were actually updated, and which input IDs
    /// were NOT present in the table (typically: row was deleted between
    /// the user clicking Queue and the request reaching the server). Used
    /// by the bulk endpoints to surface "5 of 10 items updated; 5 not
    /// found" in the UI instead of silently dropping the missing ones.
    /// </summary>
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

    // ===================================================================
    // Status transition helpers — used by every UpdateStatus variant so
    // transition semantics (LastSyncTime on Synced, Reason auto-clear,
    // optional retry-count tracking) stay consistent across modules.
    // ===================================================================

    /// <summary>
    /// Builds the universal SET clauses for a status transition. Always
    /// includes Status, StatusDate, Reason. Adds LastSyncTime when
    /// transitioning to <see cref="SyncStatus.Synced"/>. When
    /// <paramref name="trackRetryCount"/> is true, also resets RetryCount on
    /// Synced and increments it on Errored — only enable for tables that
    /// have a RetryCount column (currently Content only).
    /// </summary>
    /// <param name="status">Target status.</param>
    /// <param name="trackRetryCount">Whether to manage RetryCount column.</param>
    /// <returns>SET clause fragments to be joined with commas.</returns>
    protected static List<string> BuildStatusTransitionClauses(SyncStatus status, bool trackRetryCount = false)
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
        else if (status == SyncStatus.Errored && trackRetryCount)
        {
            clauses.Add("RetryCount = COALESCE(RetryCount, 0) + 1");
        }

        return clauses;
    }

    /// <summary>
    /// Binds the universal status-transition parameters (<c>@status</c>,
    /// <c>@statusDate</c>, <c>@reason</c>) onto <paramref name="cmd"/>.
    /// Synced auto-clears the reason regardless of the caller's input — the
    /// row's prior error text shouldn't survive a successful sync.
    /// </summary>
    protected static void AddStatusTransitionParameters(SqliteCommand cmd, SyncStatus status, string? reason)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        cmd.Parameters.AddWithValue("@status", (int)status);
        cmd.Parameters.AddWithValue("@statusDate", FormatTimestamp(DateTime.UtcNow));
        var effectiveReason = status == SyncStatus.Synced ? null : reason;
        cmd.Parameters.AddWithValue("@reason", (object?)effectiveReason ?? DBNull.Value);
    }

    // ===================================================================
    // Reader helpers — used by MapFromReader implementations to keep
    // null-handling and timestamp parsing identical across modules.
    // ===================================================================

    /// <summary>
    /// Reads a nullable string column.
    /// </summary>
    protected static string? ReadNullableString(IDataRecord reader, string column)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var ord = reader.GetOrdinal(column);
        return reader.IsDBNull(ord) ? null : reader.GetString(ord);
    }

    /// <summary>
    /// Reads a nullable Int64 column.
    /// </summary>
    protected static long? ReadNullableInt64(IDataRecord reader, string column)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var ord = reader.GetOrdinal(column);
        return reader.IsDBNull(ord) ? null : reader.GetInt64(ord);
    }

    /// <summary>
    /// Reads a nullable Int32 column.
    /// </summary>
    protected static int? ReadNullableInt32(IDataRecord reader, string column)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var ord = reader.GetOrdinal(column);
        return reader.IsDBNull(ord) ? null : reader.GetInt32(ord);
    }

    /// <summary>
    /// Reads a non-null timestamp column stored as ISO-8601 round-trip text.
    /// </summary>
    protected static DateTime ReadDateTime(IDataRecord reader, string column)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return ParseTimestamp(reader.GetString(reader.GetOrdinal(column)));
    }

    /// <summary>
    /// Reads a nullable timestamp column stored as ISO-8601 round-trip text.
    /// </summary>
    protected static DateTime? ReadNullableDateTime(IDataRecord reader, string column)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var ord = reader.GetOrdinal(column);
        return reader.IsDBNull(ord) ? null : ParseTimestamp(reader.GetString(ord));
    }

    // ===================================================================
    // Parameter helpers
    // ===================================================================

    /// <summary>
    /// Adds a nullable parameter, mapping null to <see cref="DBNull.Value"/>.
    /// </summary>
    protected static void AddNullable(SqliteCommand cmd, string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    /// <summary>
    /// Adds a timestamp parameter as ISO-8601 round-trip text.
    /// </summary>
    protected static void AddTimestamp(SqliteCommand cmd, string name, DateTime value)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        cmd.Parameters.AddWithValue(name, FormatTimestamp(value));
    }

    /// <summary>
    /// Adds a nullable timestamp parameter as ISO-8601 round-trip text or DBNull.
    /// </summary>
    protected static void AddNullableTimestamp(SqliteCommand cmd, string name, DateTime? value)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        cmd.Parameters.AddWithValue(name, value.HasValue ? FormatTimestamp(value.Value) : (object)DBNull.Value);
    }

    // ===================================================================
    // Time helpers
    // ===================================================================

    /// <summary>
    /// Formats a <see cref="DateTime"/> as ISO-8601 round-trip text using the
    /// invariant culture. The canonical wire format for all timestamp
    /// columns in this database.
    /// </summary>
    protected static string FormatTimestamp(DateTime value)
        => value.ToString("o", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses an ISO-8601 round-trip text timestamp written by
    /// <see cref="FormatTimestamp"/>. Returns <see cref="DateTime.MinValue"/>
    /// on parse failure (legacy rows occasionally have malformed dates).
    /// </summary>
    protected static DateTime ParseTimestamp(string value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : DateTime.MinValue;

    // ===================================================================
    // Helpers for subclasses
    // ===================================================================

    /// <summary>
    /// Reads all rows from <paramref name="cmd"/> using <see cref="MapFromReader"/>.
    /// </summary>
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
    /// Executes a read against the connection under the write-lock,
    /// returning <paramref name="fallback"/> on transient errors.
    /// </summary>
    protected T ExecuteRead<T>(
        Func<SqliteConnection, T> readOperation,
        T fallback,
        [CallerMemberName] string? callerName = null)
    {
        ArgumentNullException.ThrowIfNull(readOperation);
        try
        {
            lock (WriteLock)
            {
                return readOperation(Connection);
            }
        }
        catch (SqliteException ex) when (
            ex.SqliteErrorCode == 5 ||  // SQLITE_BUSY
            ex.SqliteErrorCode == 6 ||  // SQLITE_LOCKED
            ex.SqliteErrorCode == 8 ||  // SQLITE_READONLY
            ex.SqliteErrorCode == 14)   // SQLITE_CANTOPEN
        {
            Logger.LogWarning(ex, "Read '{Op}' on {Table} failed with SQLite error {Code}; returning fallback", callerName, TableName, ex.SqliteErrorCode);
            return fallback;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 11) // SQLITE_CORRUPT
        {
            Logger.LogError(ex, "Database CORRUPT during read '{Op}' on {Table}", callerName, TableName);
            return fallback;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("connection", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogWarning(ex, "Connection error during read '{Op}' on {Table}; returning fallback", callerName, TableName);
            return fallback;
        }
    }

    /// <summary>
    /// Executes a write against the connection under the write-lock. Errors
    /// propagate; callers decide retry policy.
    /// </summary>
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
            Logger.LogError(ex, "Write '{Op}' on {Table} failed with SQLite error {Code}", callerName, TableName, ex.SqliteErrorCode);
            throw;
        }
    }
}
