using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// A real sync database in a temp folder, opened through the schema the plugin runs, and removed with its folder on dispose.
/// </summary>
public sealed class SyncTestDatabase : IDisposable
{
    private readonly SyncTempFolder _folder;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncTestDatabase"/> class and creates the database.
    /// </summary>
    /// <param name="label">A short word naming the test class, so a leftover folder can be traced.</param>
    public SyncTestDatabase(string label)
    {
        _folder = new SyncTempFolder(label);
        Database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _folder.Root);
        Provider = new FixedProvider(Database);
    }

    /// <summary>
    /// Gets the database the managers under test write to.
    /// </summary>
    public SyncDatabase Database { get; }

    /// <summary>
    /// Gets the provider to hand a manager.
    /// </summary>
    public FixedProvider Provider { get; }

    /// <summary>
    /// Gets the path of the database file.
    /// </summary>
    public string FilePath => Path.Combine(_folder.Root, "serversync", "sync.db");

    /// <summary>
    /// Reads one value over a separate read only connection, so a test checks what reached the file.
    /// </summary>
    /// <param name="sql">The query, with no values from outside the test.</param>
    /// <returns>The first column of the first row, or null when there is none.</returns>
    public object? Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={FilePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Database.Dispose();
        _folder.Dispose();
    }
}
