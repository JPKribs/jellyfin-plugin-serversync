#pragma warning disable CA2100 // SQL is internal and parameterized.
using System;
using System.Data;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Runs reads and writes for the queue tables, with every write under the sync database's one write lock.
/// The workers and the controllers therefore never interleave statements on the write connection.
/// </summary>
public abstract class QueueStoreBase
{
    private readonly ISyncDatabaseProvider _databaseProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueStoreBase"/> class.
    /// </summary>
    /// <param name="databaseProvider">Opens the sync database on first use.</param>
    /// <param name="logger">Writes the store's log entries.</param>
    protected QueueStoreBase(ISyncDatabaseProvider databaseProvider, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(databaseProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _databaseProvider = databaseProvider;
        Logger = logger;
    }

    /// <summary>
    /// Gets the database, which opens on first use so one that cannot be opened fails the call that needs it.
    /// </summary>
    protected SyncDatabase Database => _databaseProvider.Database;

    /// <summary>Gets the logger.</summary>
    protected ILogger Logger { get; }

    /// <summary>Runs a read on a read connection, beside any write in progress.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="read">The statements to run on the read connection.</param>
    /// <returns>The result.</returns>
    protected T Read<T>(Func<SqliteConnection, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return Database.RunRead(read);
    }

    /// <summary>Runs a write that returns a value, such as a count of deleted rows, under the write lock.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="write">The statements to run on the write connection.</param>
    /// <returns>Its result.</returns>
    protected T Write<T>(Func<SqliteConnection, T> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        lock (Database.WriteLock)
        {
            return write(Database.Connection);
        }
    }

    /// <summary>Runs a write under the write lock.</summary>
    /// <param name="write">The statements to run on the write connection.</param>
    protected void Write(Action<SqliteConnection> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        lock (Database.WriteLock)
        {
            write(Database.Connection);
        }
    }

    /// <summary>Formats a UTC time the way every table stores it, as fixed width text whose order is time order.</summary>
    /// <param name="value">The UTC time to format.</param>
    /// <returns>The ISO 8601 text.</returns>
    protected static string Stamp(DateTime value) => Utilities.UtcTime.Format(value);

    /// <summary>Parses a stored time.</summary>
    /// <param name="value">The stored text.</param>
    /// <returns>The UTC time.</returns>
    protected static DateTime Unstamp(string value) => Utilities.UtcTime.Parse(value);

    /// <summary>Reads a nullable text column.</summary>
    /// <param name="reader">The row to read from.</param>
    /// <param name="column">The name of the column.</param>
    /// <returns>The text, or null.</returns>
    protected static string? Text(IDataRecord reader, string column)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    /// <summary>Reads a nullable time column.</summary>
    /// <param name="reader">The row to read from.</param>
    /// <param name="column">The name of the column.</param>
    /// <returns>The time, or null.</returns>
    protected static DateTime? Time(IDataRecord reader, string column)
    {
        var text = Text(reader, column);
        return text is null ? null : Unstamp(text);
    }

    /// <summary>Adds a parameter that may be null.</summary>
    /// <param name="cmd">The command the parameter is added to.</param>
    /// <param name="name">The parameter name.</param>
    /// <param name="value">The value, or null for a database null.</param>
    protected static void Add(SqliteCommand cmd, string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}
