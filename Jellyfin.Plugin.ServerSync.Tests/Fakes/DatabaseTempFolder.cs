using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// A temp folder that stands in for Jellyfin's data folder in a database test, deleted when the test ends.
/// </summary>
internal sealed class DatabaseTempFolder : IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseTempFolder"/> class.
    /// </summary>
    /// <param name="label">A word for the folder's name, so a leftover says which test made it.</param>
    public DatabaseTempFolder(string label)
    {
        DataPath = Path.Combine(Path.GetTempPath(), "serversync-" + label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataPath);
    }

    /// <summary>Gets the folder given to the database as Jellyfin's data folder.</summary>
    public string DataPath { get; }

    /// <summary>Gets the folder the database keeps its file in.</summary>
    public string DatabaseFolder => Path.Combine(DataPath, "serversync");

    /// <summary>Gets where the database file is, whether or not it exists yet.</summary>
    public string DatabaseFile => Path.Combine(DatabaseFolder, "sync.db");

    /// <summary>Opens a plain connection to the database file, beside the one under test.</summary>
    /// <returns>The open connection, which the caller disposes.</returns>
    public SqliteConnection Raw()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabaseFile, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>Runs one statement on a plain connection, with values passed as parameters.</summary>
    /// <param name="sql">The statement.</param>
    /// <param name="parameters">Each parameter's name and value.</param>
    /// <returns>The first column of the first row, or the number of rows changed when the statement returns none.</returns>
    public object? Run(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Raw();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ? command.ExecuteScalar() : command.ExecuteNonQuery();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(DataPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
