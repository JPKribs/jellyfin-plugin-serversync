using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using JPKribs.Jellyfin.Base;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Database;

/// <summary>
/// The schema is created in one step and never upgraded in place, so these tests pin what it holds and
/// what happens to a database from a release this one cannot read.
/// </summary>
public sealed class SyncSchemaTests : IDisposable
{
    // The fingerprint of every table, column, and index at schema v39. A change to the schema changes
    // it, which is the moment to raise SyncSchema.CurrentSchemaVersion and decide what older files do.
    private const string SchemaFingerprint = "4C54FF6448E55EC775EC3F840D553C1552E62198941258514ECB49B158D9208B";

    private readonly string _dataPath;
    private readonly string _dbPath;

    public SyncSchemaTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), "serversync-schema-" + Guid.NewGuid().ToString("N"));
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
    /// An empty database gets every table the plugin reads.
    /// True: a fresh install works from its first start.
    /// False: the first query against a missing table fails.
    /// </summary>
    [Fact]
    public void Create_MakesEveryTable()
    {
        using var conn = Memory();
        SyncSchema.Create(conn);

        var tables = Column(conn, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name");
        Assert.Equal(
            new[]
            {
                "HistorySyncItems", "InboundHints", "LinkStreamDays", "MetadataSyncItems", "ObjectVersions", "OutboundHints", "PeerPairings",
                "PendingVersions", "PeopleSyncItems", "PlaylistCopies", "RemovedItems", "RemovedItemsLog", "ScanRuns", "ScanWatermarks",
                "SourceMediaSegments", "SyncAttention", "SyncItems", "UserSyncItems"
            },
            tables);
        Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM RemovedItemsLog"));
    }

    /// <summary>
    /// Creating the schema twice changes nothing.
    /// True: a start that was cut short after the tables and before the version stamp recovers.
    /// False: that start would fail on a table that already exists.
    /// </summary>
    [Fact]
    public void Create_Twice_IsHarmless()
    {
        using var conn = Memory();
        SyncSchema.Create(conn);
        var first = Describe(conn);
        SyncSchema.Create(conn);

        Assert.Equal(first, Describe(conn));
        Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM RemovedItemsLog"));
    }

    /// <summary>
    /// The schema is exactly the one this version number names.
    /// True: a database written by any build at this version has the same tables, columns, and indexes.
    /// False: the schema drifted without its version, so two builds at one version would disagree.
    /// </summary>
    [Fact]
    public void Schema_MatchesItsVersion()
    {
        using var conn = Memory();
        SyncSchema.Create(conn);
        var described = Describe(conn);
        if (Environment.GetEnvironmentVariable("SERVERSYNC_SCHEMA_DUMP") is { Length: > 0 } dump)
        {
            File.WriteAllText(dump, described);
        }

        Assert.Equal(39, SyncSchema.CurrentSchemaVersion);
        Assert.Equal(SchemaFingerprint, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(described))));
    }

    /// <summary>
    /// A database from a release before the oldest one this release reads is refused and left as it was.
    /// True: installing that release and starting it once brings the same file forward, with nothing lost.
    /// False: the file would be replaced by an empty one, or opened against tables it does not have.
    /// </summary>
    [Fact]
    public void OlderDatabase_IsRefusedAndLeftUntouched()
    {
        new SyncDatabase(NullLogger<SyncDatabase>.Instance, _dataPath).Dispose();
        using (var conn = Raw())
        {
            Exec(conn, "CREATE TABLE Marker (Id INTEGER); INSERT INTO Marker VALUES (7);");
            DatabaseSchema.SetVersion(conn, SyncSchema.OldestUpgradableSchema - 1);
        }

        var refused = Assert.Throws<SchemaTooOldException>(() => new SyncDatabase(NullLogger<SyncDatabase>.Instance, _dataPath));
        Assert.Equal(SyncSchema.OldestUpgradableSchema - 1, refused.FoundVersion);
        Assert.Contains(SyncSchema.OldestUpgradableRelease, refused.Message, StringComparison.Ordinal);

        using var after = Raw();
        Assert.Equal(SyncSchema.OldestUpgradableSchema - 1, DatabaseSchema.GetVersion(after));
        Assert.Equal(7L, Scalar(after, "SELECT Id FROM Marker"));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(_dbPath)!), f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal));
    }

    /// <summary>
    /// A database from the oldest release this one reads is brought forward: the tables added since are created, and every stored row is left as it was.
    /// True: a server updating from that release keeps its tracking and gains the new tables.
    /// False: the update would hold the plugin, or drop what the server had tracked.
    /// </summary>
    [Fact]
    public void DatabaseAtTheOldestUpgradableSchema_GainsTheNewTablesAndKeepsItsRows()
    {
        new SyncDatabase(NullLogger<SyncDatabase>.Instance, _dataPath).Dispose();
        using (var conn = Raw())
        {
            Exec(conn, "DROP TABLE ScanRuns; DROP TABLE LinkStreamDays; DROP TABLE SourceMediaSegments; ALTER TABLE MetadataSyncItems DROP COLUMN SourceSegmentsValue; ALTER TABLE MetadataSyncItems DROP COLUMN LocalSegmentsValue; ALTER TABLE MetadataSyncItems DROP COLUMN SourceSegmentsHash; ALTER TABLE MetadataSyncItems DROP COLUMN SyncedSegmentsHash; CREATE TABLE Marker (Id INTEGER); INSERT INTO Marker VALUES (7);");
            DatabaseSchema.SetVersion(conn, SyncSchema.OldestUpgradableSchema);
        }

        SqliteConnection.ClearAllPools();
        Assert.False(SyncSchema.IsTooOld(SyncSchema.StoredVersion(_dataPath)));
        new SyncDatabase(NullLogger<SyncDatabase>.Instance, _dataPath).Dispose();

        using var after = Raw();
        Assert.Equal(SyncSchema.CurrentSchemaVersion, DatabaseSchema.GetVersion(after));
        Assert.Equal(SyncSchema.MinReaderVersion, DatabaseSchema.GetMinReaderVersion(after));
        Assert.Equal(7L, Scalar(after, "SELECT Id FROM Marker"));
        Assert.Equal(3L, Scalar(after, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('ScanRuns', 'LinkStreamDays', 'SourceMediaSegments')"));
        Assert.Equal(4L, Scalar(after, "SELECT COUNT(*) FROM pragma_table_info('MetadataSyncItems') WHERE name LIKE '%Segments%'"));
    }

    /// <summary>
    /// The upgrade refuses a schema older than the oldest it brings forward.
    /// True: an older database is never opened against tables it does not have.
    /// False: an old database would be stamped as current with half its tables missing.
    /// </summary>
    [Fact]
    public void Upgrade_OlderThanTheOldestUpgradableSchema_Throws()
    {
        using var conn = Memory();

        var refused = Assert.Throws<SchemaTooOldException>(() => SyncSchema.Upgrade(conn, SyncSchema.OldestUpgradableSchema - 1));

        Assert.Equal(SyncSchema.OldestUpgradableSchema - 1, refused.FoundVersion);
    }

    /// <summary>
    /// Once a database is refused, the provider answers the same way without opening the file again.
    /// True: every task and page call fails fast with the one reason.
    /// False: each call would reopen and re-read the file.
    /// </summary>
    [Fact]
    public void Provider_RemembersARefusedDatabase()
    {
        new SyncDatabase(NullLogger<SyncDatabase>.Instance, _dataPath).Dispose();
        using (var conn = Raw())
        {
            DatabaseSchema.SetVersion(conn, 20);
        }

        var (paths, pathsStub) = InterfaceStub.Create<MediaBrowser.Common.Configuration.IApplicationPaths>();
        pathsStub.Answer("get_DataPath", _ => _dataPath);
        using var provider = new SyncDatabaseProvider(NullLoggerFactory.Instance, paths);
        var first = Assert.Throws<SchemaTooOldException>(() => provider.Database);
        File.Delete(_dbPath);

        Assert.Same(first, Assert.Throws<SchemaTooOldException>(() => provider.Database));
    }

    /// <summary>
    /// The version a database holds is read without opening it for use, and only one an older release
    /// wrote counts as too old.
    /// True: the plugin knows to hold itself before anything reads the settings or the database.
    /// False: a missing, new, or current database would hold a healthy server, or an old one would not.
    /// </summary>
    [Fact]
    public void StoredVersion_TellsAnOlderDatabaseFromTheRest()
    {
        Assert.Null(SyncSchema.StoredVersion(_dataPath));
        Assert.False(SyncSchema.IsTooOld(null));
        Assert.False(SyncSchema.IsTooOld(0));

        new SyncDatabase(NullLogger<SyncDatabase>.Instance, _dataPath).Dispose();
        Assert.Equal(SyncSchema.CurrentSchemaVersion, SyncSchema.StoredVersion(_dataPath));
        Assert.False(SyncSchema.IsTooOld(SyncSchema.StoredVersion(_dataPath)));
        Assert.False(SyncSchema.IsTooOld(SyncSchema.CurrentSchemaVersion + 1));
        Assert.False(SyncSchema.IsTooOld(SyncSchema.OldestUpgradableSchema));
        Assert.True(SyncSchema.IsTooOld(SyncSchema.OldestUpgradableSchema - 1));

        using (var conn = Raw())
        {
            DatabaseSchema.SetVersion(conn, 24);
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal(24, SyncSchema.StoredVersion(_dataPath));
        Assert.True(SyncSchema.IsTooOld(SyncSchema.StoredVersion(_dataPath)));
    }

    /// <summary>
    /// A table manager or a store over a database that cannot be opened is still built, and fails only
    /// when it is used.
    /// True: Jellyfin builds its services and starts, and only the call that needs the database fails.
    /// False: building any service that holds a store would fail, which stops Jellyfin from starting.
    /// </summary>
    [Fact]
    public void StoresOverARefusedDatabase_AreBuiltAndFailOnUse()
    {
        new SyncDatabase(NullLogger<SyncDatabase>.Instance, _dataPath).Dispose();
        using (var conn = Raw())
        {
            DatabaseSchema.SetVersion(conn, 30);
        }

        var (paths, pathsStub) = InterfaceStub.Create<MediaBrowser.Common.Configuration.IApplicationPaths>();
        pathsStub.Answer("get_DataPath", _ => _dataPath);
        using var provider = new SyncDatabaseProvider(NullLoggerFactory.Instance, paths);

        var content = new ContentSyncTableManager(provider, NullLogger<ContentSyncTableManager>.Instance);
        var outbound = new Jellyfin.Plugin.ServerSync.Services.Queue.OutboundHintStore(provider, NullLogger<Jellyfin.Plugin.ServerSync.Services.Queue.OutboundHintStore>.Instance);

        Assert.Throws<SchemaTooOldException>(() => content.GetAllStrict());
        Assert.Throws<SchemaTooOldException>(() => outbound.GetAll());
    }

    private static SqliteConnection Memory()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        return conn;
    }

    private SqliteConnection Raw()
    {
        SqliteConnection.ClearAllPools();
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false }.ToString());
        conn.Open();
        return conn;
    }

    // Every column of every table in order, then every index, one per line.
    private static string Describe(SqliteConnection conn)
    {
        var lines = Column(conn, @"SELECT m.name || '.' || p.name || ' ' || p.type || ' ' || p.""notnull"" || ' ' || COALESCE(p.dflt_value, '-') || ' ' || p.pk
            FROM sqlite_master m, pragma_table_info(m.name) p
            WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%' AND m.name <> 'SchemaInfo' ORDER BY m.name, p.cid").ToList();
        lines.AddRange(Column(conn, "SELECT 'IDX ' || name || ' ' || tbl_name FROM sqlite_master WHERE type = 'index' AND tbl_name <> 'SchemaInfo' ORDER BY name"));
        return string.Join('\n', lines);
    }

    private static string[] Column(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values.ToArray();
    }

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
