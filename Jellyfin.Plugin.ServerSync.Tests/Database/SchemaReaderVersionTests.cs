using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Services;
using JPKribs.Jellyfin.Base;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Database;

/// <summary>
/// The database records the oldest schema version that can still read it, so an older build can keep
/// using a database a newer build only added to.
/// </summary>
public sealed class SchemaReaderVersionTests : IDisposable
{
    private readonly string _dataPath;
    private readonly string _dbPath;

    public SchemaReaderVersionTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), "serversync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataPath);
        _dbPath = Path.Combine(_dataPath, "serversync", "sync.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// A fresh database records this build's version and the oldest version that can read it.
    /// True: a later build knows how far back the file stays usable.
    /// False: every later downgrade sets the database aside.
    /// </summary>
    [Fact]
    public void FreshDatabase_RecordsTheMinimumReader()
    {
        Open().Dispose();
        using var conn = Raw();
        Assert.Equal(DatabaseMigrationService.CurrentSchemaVersion, DatabaseSchema.GetVersion(conn));
        Assert.Equal(DatabaseMigrationService.MinReaderVersion, DatabaseSchema.GetMinReaderVersion(conn));
    }

    /// <summary>
    /// A newer database whose marker this build meets is used as is, version and extra tables untouched.
    /// True: rolling back one build keeps the sync rows, bases, and versions.
    /// False: a downgrade throws away every row and the next upgrade starts from nothing.
    /// </summary>
    [Fact]
    public void NewerDatabase_ThisBuildCanRead_IsUsedAsIs()
    {
        Open().Dispose();
        using (var conn = Raw())
        {
            Exec(conn, "CREATE TABLE FutureTable (Id INTEGER)");
            DatabaseSchema.SetVersion(conn, DatabaseMigrationService.CurrentSchemaVersion + 1);
        }

        Open().Dispose();
        using var after = Raw();
        Assert.Equal(DatabaseMigrationService.CurrentSchemaVersion + 1, DatabaseSchema.GetVersion(after));
        Assert.Equal(1L, Scalar(after, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'FutureTable'"));
        Assert.Empty(Backups());
    }

    /// <summary>
    /// A newer database that needs a newer reader, or carries no marker at all, is set aside as before.
    /// True: this build never runs against a schema it cannot use.
    /// False: it would fail query by query against changed tables.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NewerDatabase_ThisBuildCannotRead_IsSetAside(bool dropMarker)
    {
        Open().Dispose();
        using (var conn = Raw())
        {
            Exec(conn, dropMarker ? "DROP TABLE SchemaInfo" : $"UPDATE SchemaInfo SET Value = {DatabaseMigrationService.CurrentSchemaVersion + 1}");
            DatabaseSchema.SetVersion(conn, DatabaseMigrationService.CurrentSchemaVersion + 1);
        }

        Open().Dispose();
        using var after = Raw();
        Assert.Equal(DatabaseMigrationService.CurrentSchemaVersion, DatabaseSchema.GetVersion(after));
        Assert.NotEmpty(Backups());
    }

    /// <summary>
    /// A database already at this version without the marker gets it on the next start.
    /// True: databases created before the marker existed are covered from now on.
    /// False: they would be set aside by the next downgrade although nothing changed.
    /// </summary>
    [Fact]
    public void CurrentDatabaseWithoutMarker_GetsIt()
    {
        Open().Dispose();
        using (var conn = Raw())
        {
            Exec(conn, "DROP TABLE SchemaInfo");
        }

        Open().Dispose();
        using var after = Raw();
        Assert.Equal(DatabaseMigrationService.MinReaderVersion, DatabaseSchema.GetMinReaderVersion(after));
    }

    private SyncDatabase Open() => new(NullLogger<SyncDatabase>.Instance, _dataPath);

    private SqliteConnection Raw()
    {
        SqliteConnection.ClearAllPools();
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false }.ToString());
        conn.Open();
        return conn;
    }

    private string[] Backups() => Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, "sync.db.newer-*").Where(f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal)).ToArray();

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
