using System;
using System.Data;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Opens the protected members of <see cref="QueueStoreBase"/> to a test, changing nothing about them.
/// </summary>
public sealed class QueueStoreProbe : QueueStoreBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="QueueStoreProbe"/> class.
    /// </summary>
    /// <param name="databaseProvider">Opens the database the probe runs on.</param>
    /// <param name="logger">The logger the base keeps.</param>
    public QueueStoreProbe(ISyncDatabaseProvider databaseProvider, ILogger logger)
        : base(databaseProvider, logger)
    {
    }

    /// <summary>Gets the logger the base keeps.</summary>
    public ILogger KeptLogger => Logger;

    /// <summary>Runs a read through the base.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="read">The statements to run.</param>
    /// <returns>The result.</returns>
    public T RunRead<T>(Func<SqliteConnection, T> read) => Read(read);

    /// <summary>Runs a write that returns a value through the base.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="write">The statements to run.</param>
    /// <returns>The result.</returns>
    public T RunWrite<T>(Func<SqliteConnection, T> write) => Write(write);

    /// <summary>Runs a write through the base.</summary>
    /// <param name="write">The statements to run.</param>
    public void RunWrite(Action<SqliteConnection> write) => Write(write);

    /// <summary>Formats a time through the base.</summary>
    /// <param name="value">The time to format.</param>
    /// <returns>The stored text.</returns>
    public static string StampOf(DateTime value) => Stamp(value);

    /// <summary>Parses a stored time through the base.</summary>
    /// <param name="value">The stored text.</param>
    /// <returns>The time.</returns>
    public static DateTime UnstampOf(string value) => Unstamp(value);

    /// <summary>Reads a nullable text column through the base.</summary>
    /// <param name="reader">The row to read from.</param>
    /// <param name="column">The column name.</param>
    /// <returns>The text, or null.</returns>
    public static string? TextOf(IDataRecord reader, string column) => Text(reader, column);

    /// <summary>Reads a nullable time column through the base.</summary>
    /// <param name="reader">The row to read from.</param>
    /// <param name="column">The column name.</param>
    /// <returns>The time, or null.</returns>
    public static DateTime? TimeOf(IDataRecord reader, string column) => Time(reader, column);

    /// <summary>Adds a parameter through the base.</summary>
    /// <param name="cmd">The command the parameter is added to.</param>
    /// <param name="name">The parameter name.</param>
    /// <param name="value">The value, or null.</param>
    public static void AddTo(SqliteCommand cmd, string name, object? value) => Add(cmd, name, value);
}
