using System;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

#pragma warning disable CA2100 // SQL commands use only internal constants and safe parameterized queries

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Handles database schema migrations for the sync database. Upgrades from
/// pre-v19 versions drop all tracking tables and recreate them; the next
/// refresh re-populates everything from source/local state.
/// </summary>
public static class DatabaseMigrationService
{
    /// <summary>
    /// Current schema version. Increment this when adding new migrations.
    /// </summary>
    public const int CurrentSchemaVersion = 21;

    /// <summary>
    /// Creates the initial database schema including all tables for the current version.
    /// </summary>
    /// <param name="connection">Database connection.</param>
    public static void CreateInitialSchema(SqliteConnection connection)
    {
        // ===== Content Sync (SyncItems) =====
        // Tracks files to be downloaded/replaced/deleted on the local server.
        // Change detection uses Size only (ETag was removed in v18 — it was
        // unstable because Jellyfin's ETag changes when UserData changes;
        // SourceModifyDate was dropped in v19 — it was never read by the
        // model and was only written as a placeholder).
        // PendingType describes the operation (download/replacement/deletion)
        // which is orthogonal to Status (Pending/Queued/Synced/Errored/Ignored).
        using var syncItemsCmd = connection.CreateCommand();
        syncItemsCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS SyncItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceLibraryId TEXT NOT NULL,
                LocalLibraryId TEXT NOT NULL,
                SourceItemId TEXT NOT NULL,
                SourcePath TEXT NOT NULL,
                SourceSize INTEGER NOT NULL,
                SourceCreateDate TEXT NOT NULL,
                LocalItemId TEXT,
                LocalPath TEXT,
                Status INTEGER NOT NULL,
                StatusDate TEXT NOT NULL,
                LastSyncTime TEXT,
                Reason TEXT,
                PendingType INTEGER,
                RetryCount INTEGER DEFAULT 0,
                CompanionFiles TEXT,
                UNIQUE(SourceItemId)
            );
            CREATE INDEX IF NOT EXISTS idx_source_item ON SyncItems(SourceItemId);
            CREATE INDEX IF NOT EXISTS idx_status ON SyncItems(Status);
            CREATE INDEX IF NOT EXISTS idx_source_library ON SyncItems(SourceLibraryId);
            CREATE INDEX IF NOT EXISTS idx_local_path ON SyncItems(LocalPath);
        ";
        syncItemsCmd.ExecuteNonQuery();

        // ===== History Sync (HistorySyncItems) =====
        // One row per (user, library, item). Source/Local/Merged are five
        // primitive fields (IsPlayed/PlayCount/PositionTicks/LastPlayedDate/
        // IsFavorite). SourceStateHash/SyncedStateHash bundle the five
        // source-side fields into a single fingerprint so Refresh can
        // skip rows whose source state hasn't moved since the last sync.
        using var historyCmd = connection.CreateCommand();
        historyCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS HistorySyncItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceUserId TEXT NOT NULL,
                LocalUserId TEXT NOT NULL,
                SourceLibraryId TEXT NOT NULL,
                LocalLibraryId TEXT NOT NULL,
                SourceItemId TEXT NOT NULL,
                LocalItemId TEXT,
                ItemName TEXT,
                SourcePath TEXT,
                LocalPath TEXT,
                SourceIsPlayed INTEGER,
                SourcePlayCount INTEGER,
                SourcePlaybackPositionTicks INTEGER,
                SourceLastPlayedDate TEXT,
                SourceIsFavorite INTEGER,
                LocalIsPlayed INTEGER,
                LocalPlayCount INTEGER,
                LocalPlaybackPositionTicks INTEGER,
                LocalLastPlayedDate TEXT,
                LocalIsFavorite INTEGER,
                MergedIsPlayed INTEGER,
                MergedPlayCount INTEGER,
                MergedPlaybackPositionTicks INTEGER,
                MergedLastPlayedDate TEXT,
                MergedIsFavorite INTEGER,
                Status INTEGER NOT NULL,
                StatusDate TEXT NOT NULL,
                LastSyncTime TEXT,
                Reason TEXT,
                SourceStateHash TEXT,
                SyncedStateHash TEXT,
                UNIQUE(SourceUserId, SourceItemId)
            );
            CREATE INDEX IF NOT EXISTS idx_history_user ON HistorySyncItems(SourceUserId, LocalUserId);
            CREATE INDEX IF NOT EXISTS idx_history_status ON HistorySyncItems(Status);
            CREATE INDEX IF NOT EXISTS idx_history_library ON HistorySyncItems(SourceLibraryId);
        ";
        historyCmd.ExecuteNonQuery();

        // ===== User Sync (UserSyncItems) =====
        // One row per (user mapping, property category). Categories are Policy,
        // Configuration, ProfileImage. SourceValueHash/SyncedValueHash provide
        // the fast-path skip; SourceImageHash/LocalImageHash/SyncedImageHash
        // remain for ProfileImage-specific comparison.
        using var userCmd = connection.CreateCommand();
        userCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS UserSyncItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceUserId TEXT NOT NULL,
                LocalUserId TEXT NOT NULL,
                SourceUserName TEXT,
                LocalUserName TEXT,
                PropertyCategory TEXT NOT NULL,
                SourceValue TEXT,
                LocalValue TEXT,
                MergedValue TEXT,
                SourceValueHash TEXT,
                SyncedValueHash TEXT,
                SourceImageHash TEXT,
                LocalImageHash TEXT,
                SyncedImageHash TEXT,
                SourceImageSize INTEGER,
                LocalImageSize INTEGER,
                SyncedImageSize INTEGER,
                Status INTEGER NOT NULL DEFAULT 0,
                StatusDate TEXT NOT NULL,
                LastSyncTime TEXT,
                Reason TEXT,
                UNIQUE(SourceUserId, LocalUserId, PropertyCategory)
            );
            CREATE INDEX IF NOT EXISTS idx_user_sync_mapping ON UserSyncItems(SourceUserId, LocalUserId);
            CREATE INDEX IF NOT EXISTS idx_user_sync_status ON UserSyncItems(Status);
            CREATE INDEX IF NOT EXISTS idx_user_sync_category ON UserSyncItems(PropertyCategory);
        ";
        userCmd.ExecuteNonQuery();

        // ===== People Sync (PeopleSyncItems) =====
        // One row per person, matched by name across servers. Two SyncableValue
        // fields: Metadata (JSON blob) and Images (JSON manifest). Hashes
        // enable the SourceHash == SyncedHash fast-path.
        using var peopleCmd = connection.CreateCommand();
        peopleCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS PeopleSyncItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PersonName TEXT NOT NULL,
                SourcePersonId TEXT,
                LocalPersonId TEXT,
                SourceMetadataValue TEXT,
                LocalMetadataValue TEXT,
                SourceMetadataHash TEXT,
                SyncedMetadataHash TEXT,
                SourceImagesValue TEXT,
                LocalImagesValue TEXT,
                SourceImagesHash TEXT,
                SyncedImagesHash TEXT,
                Status INTEGER NOT NULL DEFAULT 0,
                StatusDate TEXT NOT NULL,
                LastSyncTime TEXT,
                Reason TEXT,
                UNIQUE(PersonName)
            );
            CREATE INDEX IF NOT EXISTS idx_people_sync_name ON PeopleSyncItems(PersonName);
            CREATE INDEX IF NOT EXISTS idx_people_sync_status ON PeopleSyncItems(Status);
        ";
        peopleCmd.ExecuteNonQuery();

        // ===== Metadata Sync (MetadataSyncItems) =====
        // One row per item, four SyncableValue fields (Metadata, Images,
        // People, Studios). Hashes per category enable per-field
        // short-circuiting. SourceETag removed — we use SourceMetadataHash
        // for the same purpose with a stable signal.
        using var metadataCmd = connection.CreateCommand();
        metadataCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS MetadataSyncItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceLibraryId TEXT NOT NULL,
                LocalLibraryId TEXT NOT NULL,
                SourceItemId TEXT NOT NULL,
                LocalItemId TEXT,
                ItemName TEXT,
                SourcePath TEXT,
                LocalPath TEXT,
                ItemType TEXT,
                IsFolder INTEGER NOT NULL DEFAULT 0,
                SourceMetadataValue TEXT,
                LocalMetadataValue TEXT,
                SourceMetadataHash TEXT,
                SyncedMetadataHash TEXT,
                SourceImagesValue TEXT,
                LocalImagesValue TEXT,
                SourceImagesHash TEXT,
                SyncedImagesHash TEXT,
                SourcePeopleValue TEXT,
                LocalPeopleValue TEXT,
                SourcePeopleHash TEXT,
                SyncedPeopleHash TEXT,
                SourceStudiosValue TEXT,
                LocalStudiosValue TEXT,
                SourceStudiosHash TEXT,
                SyncedStudiosHash TEXT,
                Status INTEGER NOT NULL DEFAULT 0,
                StatusDate TEXT NOT NULL,
                LastSyncTime TEXT,
                Reason TEXT,
                UNIQUE(SourceLibraryId, SourceItemId)
            );
            CREATE INDEX IF NOT EXISTS idx_metadata_sync_item ON MetadataSyncItems(SourceItemId);
            CREATE INDEX IF NOT EXISTS idx_metadata_sync_status ON MetadataSyncItems(Status);
            CREATE INDEX IF NOT EXISTS idx_metadata_sync_library ON MetadataSyncItems(SourceLibraryId);
        ";
        metadataCmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Gets the current schema version from the database.
    /// </summary>
    /// <param name="connection">Database connection.</param>
    /// <returns>Current schema version number.</returns>
    public static int GetSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var result = command.ExecuteScalar();
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// Sets the schema version in the database.
    /// </summary>
    /// <param name="connection">Database connection.</param>
    /// <param name="version">Version number to set.</param>
    public static void SetSchemaVersion(SqliteConnection connection, int version)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version = {version}";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Migrates the database schema from an older version to the current version.
    /// v18 is a hard reset — any older version is dropped and recreated.
    /// </summary>
    /// <param name="connection">Database connection.</param>
    /// <param name="fromVersion">Version to migrate from.</param>
    /// <param name="logger">Logger for migration messages.</param>
    /// <returns>True if migration succeeded, false if failed.</returns>
    public static bool MigrateSchema(SqliteConnection connection, int fromVersion, ILogger logger)
    {
        logger.LogInformation("Migrating database schema from v{From} to v{To}", fromVersion, CurrentSchemaVersion);

        try
        {
            // Pre-v19 schemas all need a hard reset: v19 dropped the
            // SourceModifyDate/SourceETag NOT NULL columns from SyncItems and
            // re-shaped several other tables. Trying to ALTER in place is
            // riskier than rebuilding from source — the next refresh
            // repopulates everything cleanly.
            if (fromVersion < 19)
            {
                logger.LogWarning(
                    "Schema upgrade to v{Target}: dropping all sync tracking tables (was v{From}). Sync tracking data will be lost; the next refresh will repopulate everything from source/local state.",
                    CurrentSchemaVersion,
                    fromVersion);

                using (var dropTransaction = connection.BeginTransaction())
                {
                    foreach (var table in new[]
                    {
                        "SyncItems",
                        "HistorySyncItems",
                        "UserSyncItems",
                        "PeopleSyncItems",
                        "MetadataSyncItems"
                    })
                    {
                        using var dropCmd = connection.CreateCommand();
                        dropCmd.Transaction = dropTransaction;
                        dropCmd.CommandText = $"DROP TABLE IF EXISTS {table}";
                        dropCmd.ExecuteNonQuery();
                    }

                    dropTransaction.Commit();
                }

                CreateInitialSchema(connection);
            }

            // v20: Clear poisoned SyncedHash columns left over from a 10.11.54
            // bug that set SyncedHash = SourceHash on un-applied categories.
            // Nulls force the next Refresh through the comparator for every
            // row; matching rows get re-MarkSynced, diverging rows requeue.
            // Status is left alone so Ignored overrides survive.
            if (fromVersion >= 19 && fromVersion < 20)
            {
                logger.LogWarning(
                    "Schema upgrade to v20: clearing poisoned SyncedHash columns left over from 10.11.54. The next Refresh will re-evaluate every row against the source via comparator (no data loss; Ignored overrides preserved).");

                using var clearTransaction = connection.BeginTransaction();

                using (var clearMetadata = connection.CreateCommand())
                {
                    clearMetadata.Transaction = clearTransaction;
                    clearMetadata.CommandText = @"
                        UPDATE MetadataSyncItems
                        SET SyncedMetadataHash = NULL,
                            SyncedImagesHash = NULL,
                            SyncedPeopleHash = NULL,
                            SyncedStudiosHash = NULL";
                    var rows = clearMetadata.ExecuteNonQuery();
                    logger.LogInformation("v20: cleared SyncedHash on {Rows} MetadataSyncItems rows", rows);
                }

                using (var clearPeople = connection.CreateCommand())
                {
                    clearPeople.Transaction = clearTransaction;
                    clearPeople.CommandText = @"
                        UPDATE PeopleSyncItems
                        SET SyncedMetadataHash = NULL,
                            SyncedImagesHash = NULL";
                    var rows = clearPeople.ExecuteNonQuery();
                    logger.LogInformation("v20: cleared SyncedHash on {Rows} PeopleSyncItems rows", rows);
                }

                clearTransaction.Commit();
            }

            // v21: History migrates to SyncableValue<string> for change
            // detection. New columns SourceStateHash + SyncedStateHash on
            // HistorySyncItems carry the source-state bundle's fingerprint.
            // Also clear any UserSyncItems SyncedValueHash values because the
            // hash format changed from truncated-SHA256 (32 hex) to full
            // JsonBlobComparator SHA256 (64 hex) — the next Refresh re-seeds
            // them via the comparator path.
            if (fromVersion >= 19 && fromVersion < 21)
            {
                logger.LogWarning(
                    "Schema upgrade to v21: adding SourceStateHash/SyncedStateHash columns to HistorySyncItems and clearing stale SyncedValueHash on UserSyncItems for the SyncableValue migration.");

                using var v21Transaction = connection.BeginTransaction();

                foreach (var col in new[] { "SourceStateHash", "SyncedStateHash" })
                {
                    using var addCol = connection.CreateCommand();
                    addCol.Transaction = v21Transaction;
                    addCol.CommandText = $"ALTER TABLE HistorySyncItems ADD COLUMN {col} TEXT";
                    try
                    {
                        addCol.ExecuteNonQuery();
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
                    {
                        // Idempotent — running against a partially-migrated
                        // schema (rare but possible from a crashed upgrade)
                        // shouldn't fail.
                    }
                }

                using (var clearUser = connection.CreateCommand())
                {
                    clearUser.Transaction = v21Transaction;
                    clearUser.CommandText = "UPDATE UserSyncItems SET SyncedValueHash = NULL";
                    var rows = clearUser.ExecuteNonQuery();
                    logger.LogInformation("v21: cleared SyncedValueHash on {Rows} UserSyncItems rows", rows);
                }

                v21Transaction.Commit();
            }

            SetSchemaVersion(connection, CurrentSchemaVersion);
            logger.LogInformation("Database migration completed successfully");
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 11 || ex.SqliteErrorCode == 26)
        {
            // Corrupt database: signal the caller to recreate.
            logger.LogError(ex, "Database migration failed on a corrupt database");
            return false;
        }
    }
}
