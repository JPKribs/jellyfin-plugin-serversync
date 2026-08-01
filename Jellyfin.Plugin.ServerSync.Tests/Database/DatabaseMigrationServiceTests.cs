using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Database;

public class DatabaseMigrationServiceTests
{
    private static SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        return conn;
    }

    private static int GetVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void SetVersion(SqliteConnection conn, int v)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {v}";
        cmd.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> GetTableNames(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        var names = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static IReadOnlyList<string> GetColumnNames(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        var cols = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            cols.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        return cols;
    }

    /// <summary>
    /// Pins the current schema version constant.
    /// True: the version constant matches what every migration target expects.
    /// False: a future bump forgot to update this test, hiding the need to think through migration paths.
    /// </summary>
    [Fact]
    public void CurrentSchemaVersion_IsTwentyTwo()
    {
        Assert.Equal(22, DatabaseMigrationService.CurrentSchemaVersion);
    }

    /// <summary>
    /// GetSchemaVersion reads SQLite PRAGMA user_version.
    /// True: callers can detect a stale schema and decide whether to migrate.
    /// False: every plugin start would mis-detect schema state and either skip or re-run migrations.
    /// </summary>
    [Fact]
    public void GetSchemaVersion_ReadsPragmaUserVersion()
    {
        using var conn = OpenConnection();
        SetVersion(conn, 17);

        Assert.Equal(17, DatabaseMigrationService.GetSchemaVersion(conn));
    }

    /// <summary>
    /// SetSchemaVersion writes SQLite PRAGMA user_version.
    /// True: a completed migration's version-bump persists so the next plugin start sees current state.
    /// False: version-bumps wouldn't stick and migrations would re-run every plugin start.
    /// </summary>
    [Fact]
    public void SetSchemaVersion_WritesPragmaUserVersion()
    {
        using var conn = OpenConnection();

        DatabaseMigrationService.SetSchemaVersion(conn, 42);

        Assert.Equal(42, GetVersion(conn));
    }

    /// <summary>
    /// CreateInitialSchema produces every sync table for fresh installs.
    /// True: a fresh database can immediately accept writes from any module.
    /// False: missing tables would crash the first refresh on a clean install.
    /// </summary>
    [Fact]
    public void CreateInitialSchema_CreatesAllSyncTables()
    {
        using var conn = OpenConnection();

        DatabaseMigrationService.CreateInitialSchema(conn);

        var tables = GetTableNames(conn);
        Assert.Contains("SyncItems", tables);
        Assert.Contains("HistorySyncItems", tables);
        Assert.Contains("UserSyncItems", tables);
        Assert.Contains("PeopleSyncItems", tables);
        Assert.Contains("MetadataSyncItems", tables);
    }

    /// <summary>
    /// Fresh-install HistorySyncItems has the v21 SyncableValue hash columns.
    /// True: the upsert SQL works on day-1 without needing an immediate ALTER pass.
    /// False: first refresh on a fresh install would crash with "no such column SourceStateHash".
    /// </summary>
    [Fact]
    public void CreateInitialSchema_HistoryTable_HasSourceStateHashColumns()
    {
        using var conn = OpenConnection();

        DatabaseMigrationService.CreateInitialSchema(conn);

        var cols = GetColumnNames(conn, "HistorySyncItems");
        Assert.Contains("SourceStateHash", cols);
        Assert.Contains("SyncedStateHash", cols);
    }

    /// <summary>
    /// Fresh-install UserSyncItems has the SourceValueHash/SyncedValueHash columns.
    /// True: the upsert SQL works on day-1 for Policy/Configuration rows.
    /// False: first refresh on a fresh install would crash with "no such column SourceValueHash".
    /// </summary>
    [Fact]
    public void CreateInitialSchema_UserTable_HasSourceValueHashColumns()
    {
        using var conn = OpenConnection();

        DatabaseMigrationService.CreateInitialSchema(conn);

        var cols = GetColumnNames(conn, "UserSyncItems");
        Assert.Contains("SourceValueHash", cols);
        Assert.Contains("SyncedValueHash", cols);
    }

    /// <summary>
    /// fromVersion=18 drops old tables and recreates the canonical schema.
    /// True: pre-v19 databases upgrade cleanly without leftover columns from the prior shape.
    /// False: leftover columns would mismatch the new SQL and produce silent column-not-found bugs.
    /// </summary>
    [Fact]
    public void MigrateSchema_FromV18_HardResetsAndRecreatesAllTables()
    {
        using var conn = OpenConnection();

        using (var stale = conn.CreateCommand())
        {
            stale.CommandText = "CREATE TABLE SyncItems (OldColumn TEXT)";
            stale.ExecuteNonQuery();
        }

        var ok = DatabaseMigrationService.MigrateSchema(conn, fromVersion: 18, NullLogger.Instance);

        Assert.True(ok);
        var cols = GetColumnNames(conn, "SyncItems");
        Assert.DoesNotContain("OldColumn", cols);
        Assert.Contains("SourceItemId", cols);
        Assert.Contains("PendingType", cols);
        Assert.Equal(22, GetVersion(conn));
    }

    /// <summary>
    /// Migrating from v18 (hard reset) leaves all expected tables in place.
    /// True: every sync module has its target table after the reset.
    /// False: a missing table would crash the first post-upgrade refresh for that module.
    /// </summary>
    [Fact]
    public void MigrateSchema_FromV18_CreatesAllExpectedTables()
    {
        using var conn = OpenConnection();

        var ok = DatabaseMigrationService.MigrateSchema(conn, fromVersion: 18, NullLogger.Instance);

        Assert.True(ok);
        var tables = GetTableNames(conn);
        Assert.Contains("SyncItems", tables);
        Assert.Contains("HistorySyncItems", tables);
        Assert.Contains("UserSyncItems", tables);
        Assert.Contains("PeopleSyncItems", tables);
        Assert.Contains("MetadataSyncItems", tables);
    }

    /// <summary>
    /// Migrating from v19 clears Metadata SyncedHashes AND alters History AND clears UserSync hashes.
    /// True: every v19→v21 step runs in sequence, leaving the database in correct v21 shape.
    /// False: skipping any step leaves stale poisoned hashes or missing columns that break apply.
    /// </summary>
    [Fact]
    public void MigrateSchema_FromV19_ClearsMetadataSyncedHashAndAltersHistory()
    {
        using var conn = OpenConnection();

        DatabaseMigrationService.CreateInitialSchema(conn);
        SetVersion(conn, 19);

        using (var seed = conn.CreateCommand())
        {
            seed.CommandText = @"
                INSERT INTO MetadataSyncItems (
                    SourceLibraryId, LocalLibraryId, SourceItemId,
                    SyncedMetadataHash, SyncedImagesHash, SyncedPeopleHash, SyncedStudiosHash,
                    Status, StatusDate
                ) VALUES (
                    'sl', 'll', 'si', 'meta-h', 'img-h', 'ppl-h', 'std-h',
                    0, '2025-01-01T00:00:00Z'
                )";
            seed.ExecuteNonQuery();
        }

        using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "ALTER TABLE HistorySyncItems DROP COLUMN SourceStateHash";
            drop.ExecuteNonQuery();
        }

        using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "ALTER TABLE HistorySyncItems DROP COLUMN SyncedStateHash";
            drop.ExecuteNonQuery();
        }

        using (var seed = conn.CreateCommand())
        {
            seed.CommandText = @"
                INSERT INTO UserSyncItems (
                    SourceUserId, LocalUserId, PropertyCategory,
                    SyncedValueHash, Status, StatusDate
                ) VALUES (
                    'su', 'lu', 'Policy', 'stale-hash', 0, '2025-01-01T00:00:00Z'
                )";
            seed.ExecuteNonQuery();
        }

        var ok = DatabaseMigrationService.MigrateSchema(conn, fromVersion: 19, NullLogger.Instance);

        Assert.True(ok);

        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT SyncedMetadataHash, SyncedImagesHash, SyncedPeopleHash, SyncedStudiosHash FROM MetadataSyncItems";
            using var reader = check.ExecuteReader();
            Assert.True(reader.Read());
            for (var i = 0; i < 4; i++)
            {
                Assert.True(reader.IsDBNull(i), $"column {i} should have been cleared to NULL");
            }
        }

        var historyCols = GetColumnNames(conn, "HistorySyncItems");
        Assert.Contains("SourceStateHash", historyCols);
        Assert.Contains("SyncedStateHash", historyCols);

        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT SyncedValueHash FROM UserSyncItems";
            using var reader = check.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.IsDBNull(0));
        }

        Assert.Equal(22, GetVersion(conn));
    }

    /// <summary>
    /// fromVersion=20 preserves Metadata SyncedHashes (v20 already cleared them) but still ALTERs History.
    /// True: v20→v21 only runs the v21 step; previously-cleared and re-seeded Metadata hashes survive.
    /// False: re-clearing Metadata hashes on every minor migration would invalidate the fast path.
    /// </summary>
    [Fact]
    public void MigrateSchema_FromV20_DoesNotClearMetadataSyncedHashAgain()
    {
        using var conn = OpenConnection();

        DatabaseMigrationService.CreateInitialSchema(conn);

        using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "ALTER TABLE HistorySyncItems DROP COLUMN SourceStateHash";
            drop.ExecuteNonQuery();
        }

        using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "ALTER TABLE HistorySyncItems DROP COLUMN SyncedStateHash";
            drop.ExecuteNonQuery();
        }

        SetVersion(conn, 20);

        using (var seed = conn.CreateCommand())
        {
            seed.CommandText = @"
                INSERT INTO MetadataSyncItems (
                    SourceLibraryId, LocalLibraryId, SourceItemId,
                    SyncedMetadataHash, Status, StatusDate
                ) VALUES (
                    'sl', 'll', 'si', 'fresh-hash', 0, '2025-01-01T00:00:00Z'
                )";
            seed.ExecuteNonQuery();
        }

        var ok = DatabaseMigrationService.MigrateSchema(conn, fromVersion: 20, NullLogger.Instance);

        Assert.True(ok);

        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT SyncedMetadataHash FROM MetadataSyncItems";
            var v = check.ExecuteScalar();
            Assert.Equal("fresh-hash", v);
        }

        var historyCols = GetColumnNames(conn, "HistorySyncItems");
        Assert.Contains("SourceStateHash", historyCols);
        Assert.Equal(22, GetVersion(conn));
    }

    /// <summary>
    /// v20→v21 also clears UserSync SyncedValueHash (the hash format change requires it).
    /// True: stale truncated SHA hashes don't falsely short-circuit on the next refresh.
    /// False: surviving truncated hashes would mismatch the new full-SHA format and silently re-evaluate every row.
    /// </summary>
    [Fact]
    public void MigrateSchema_FromV20_ClearsUserSyncedValueHash()
    {
        using var conn = OpenConnection();
        DatabaseMigrationService.CreateInitialSchema(conn);

        using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "ALTER TABLE HistorySyncItems DROP COLUMN SourceStateHash";
            drop.ExecuteNonQuery();
        }

        using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "ALTER TABLE HistorySyncItems DROP COLUMN SyncedStateHash";
            drop.ExecuteNonQuery();
        }

        SetVersion(conn, 20);

        using (var seed = conn.CreateCommand())
        {
            seed.CommandText = @"
                INSERT INTO UserSyncItems (
                    SourceUserId, LocalUserId, PropertyCategory,
                    SyncedValueHash, Status, StatusDate
                ) VALUES (
                    'su', 'lu', 'Policy', 'truncated-32-char-hash', 0, '2025-01-01T00:00:00Z'
                )";
            seed.ExecuteNonQuery();
        }

        var ok = DatabaseMigrationService.MigrateSchema(conn, fromVersion: 20, NullLogger.Instance);

        Assert.True(ok);

        using var check = conn.CreateCommand();
        check.CommandText = "SELECT SyncedValueHash FROM UserSyncItems";
        using var reader = check.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
    }

    /// <summary>
    /// Running the v21 ALTER twice (column already exists) returns success.
    /// True: a half-finished previous run doesn't break the next attempt.
    /// False: a duplicate-column SqliteException would fail the entire migration and leave state inconsistent.
    /// </summary>
    [Fact]
    public void MigrateSchema_FromV20_Idempotent_IfColumnsAlreadyExist()
    {
        using var conn = OpenConnection();
        DatabaseMigrationService.CreateInitialSchema(conn);
        SetVersion(conn, 20);

        var ok = DatabaseMigrationService.MigrateSchema(conn, fromVersion: 20, NullLogger.Instance);

        Assert.True(ok);
        Assert.Equal(22, GetVersion(conn));
    }

    /// <summary>
    /// fromVersion=21 (already current) returns success without doing migration work.
    /// True: plugin startup on the latest version is a fast no-op.
    /// False: re-running migrations on every startup would crash or corrupt state.
    /// </summary>
    [Fact]
    public void MigrateSchema_FromCurrent_NoOp_StillReturnsOk()
    {
        using var conn = OpenConnection();
        DatabaseMigrationService.CreateInitialSchema(conn);
        SetVersion(conn, 22);

        var ok = DatabaseMigrationService.MigrateSchema(conn, fromVersion: 22, NullLogger.Instance);

        Assert.True(ok);
        Assert.Equal(22, GetVersion(conn));
    }

    /// <summary>
    /// v21 to v22 adds RetryCount to the four tables that lacked it.
    /// True: every module gets the retry ceiling Content already had.
    /// False: the ceiling silently does nothing outside Content and a row that
    /// can never converge is re-applied on every run forever.
    /// </summary>
    [Fact]
    public void MigrateSchema_FromV21_AddsRetryCountToEveryTable()
    {
        using var conn = OpenConnection();
        DatabaseMigrationService.CreateInitialSchema(conn);
        foreach (var table in new[] { "HistorySyncItems", "UserSyncItems", "PeopleSyncItems", "MetadataSyncItems" })
        {
            using var drop = conn.CreateCommand();
            drop.CommandText = $"ALTER TABLE {table} DROP COLUMN RetryCount";
            drop.ExecuteNonQuery();
        }

        SetVersion(conn, 21);

        var ok = DatabaseMigrationService.MigrateSchema(conn, fromVersion: 21, NullLogger.Instance);

        Assert.True(ok);
        Assert.Equal(22, GetVersion(conn));
        foreach (var table in new[] { "HistorySyncItems", "UserSyncItems", "PeopleSyncItems", "MetadataSyncItems" })
        {
            Assert.Contains("RetryCount", GetColumnNames(conn, table));
        }
    }

    /// <summary>
    /// Re-running the v22 ALTER against tables that already have the column
    /// succeeds, so a crashed upgrade can be resumed.
    /// True: a partially applied upgrade recovers on the next start.
    /// False: the plugin is stuck failing migration on every boot.
    /// </summary>
    [Fact]
    public void MigrateSchema_FromV21_Idempotent_WhenRetryCountAlreadyExists()
    {
        using var conn = OpenConnection();
        DatabaseMigrationService.CreateInitialSchema(conn);
        SetVersion(conn, 21);

        var ok = DatabaseMigrationService.MigrateSchema(conn, fromVersion: 21, NullLogger.Instance);

        Assert.True(ok);
        Assert.Equal(22, GetVersion(conn));
    }
}
