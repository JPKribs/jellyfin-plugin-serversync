#pragma warning disable CA2100 // SQL is internal and parameterized.
using System;
using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Shared plumbing for the queue tables. Every read and write runs under the sync database's one
/// write lock, the same way the sync table managers do, so the workers and the controllers never
/// interleave statements on the shared connection.
/// </summary>
public abstract class QueueStoreBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="QueueStoreBase"/> class.
    /// </summary>
    /// <param name="databaseProvider">The database provider.</param>
    /// <param name="logger">Logger.</param>
    protected QueueStoreBase(ISyncDatabaseProvider databaseProvider, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(databaseProvider);
        Database = databaseProvider.Database;
        Logger = logger;
    }

    /// <summary>Gets the database.</summary>
    protected SyncDatabase Database { get; }

    /// <summary>Gets the logger.</summary>
    protected ILogger Logger { get; }

    /// <summary>Runs a read under the write lock.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="read">The read.</param>
    /// <returns>The result.</returns>
    protected T Read<T>(Func<SqliteConnection, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (Database.WriteLock)
        {
            return read(Database.Connection);
        }
    }

    /// <summary>Runs a write under the write lock.</summary>
    /// <param name="write">The write.</param>
    /// <summary>Runs a write that returns a value, such as a count of deleted rows, under the write lock.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="write">The write.</param>
    /// <returns>Its result.</returns>
    protected T Write<T>(Func<SqliteConnection, T> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        lock (Database.WriteLock)
        {
            return write(Database.Connection);
        }
    }

    protected void Write(Action<SqliteConnection> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        lock (Database.WriteLock)
        {
            write(Database.Connection);
        }
    }

    /// <summary>Formats a UTC time the way every table stores it.</summary>
    /// <param name="value">The time.</param>
    /// <returns>The ISO 8601 text.</returns>
    protected static string Stamp(DateTime value) => Utilities.UtcTime.Format(value);

    /// <summary>Parses a stored time.</summary>
    /// <param name="value">The stored text.</param>
    /// <returns>The UTC time.</returns>
    protected static DateTime Unstamp(string value) => Utilities.UtcTime.Parse(value);

    /// <summary>Reads a nullable text column.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="column">The column.</param>
    /// <returns>The text, or null.</returns>
    protected static string? Text(IDataRecord reader, string column)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    /// <summary>Reads a nullable time column.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="column">The column.</param>
    /// <returns>The time, or null.</returns>
    protected static DateTime? Time(IDataRecord reader, string column)
    {
        var text = Text(reader, column);
        return text is null ? null : Unstamp(text);
    }

    /// <summary>Adds a parameter that may be null.</summary>
    /// <param name="cmd">The command.</param>
    /// <param name="name">The parameter name.</param>
    /// <param name="value">The value.</param>
    protected static void Add(SqliteCommand cmd, string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}
