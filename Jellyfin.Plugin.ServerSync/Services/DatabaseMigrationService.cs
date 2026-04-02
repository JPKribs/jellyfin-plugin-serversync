using System;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

#pragma warning disable CA2100 // SQL commands use only internal constants and safe parameterized queries

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Handles database schema migrations for the sync database.
/// </summary>
public static class DatabaseMigrationService
{
    /// <summary>
    /// Current schema version. Increment this when adding new migrations.
    /// </summary>
    public const int CurrentSchemaVersion = 17;

    /// <summary>
    /// Creates the initial database schema including all tables for the current version.
    /// </summary>
    /// <param name="connection">Database connection.</param>
    public static void CreateInitialSchema(SqliteConnection connection)
    {
        // Create SyncItems table (Content Sync)
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
                SourceModifyDate TEXT NOT NULL,
                SourceETag TEXT,
                LocalItemId TEXT,
                LocalPath TEXT,
                StatusDate TEXT NOT NULL,
                Status INTEGER NOT NULL,
                PendingType INTEGER,
                LastSyncTime TEXT,
                ErrorMessage TEXT,
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

        // Create HistorySyncItems table (History Sync)
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
                ErrorMessage TEXT,
                UNIQUE(SourceUserId, SourceItemId)
            );
            CREATE INDEX IF NOT EXISTS idx_history_user ON HistorySyncItems(SourceUserId, LocalUserId);
            CREATE INDEX IF NOT EXISTS idx_history_status ON HistorySyncItems(Status);
            CREATE INDEX IF NOT EXISTS idx_history_library ON HistorySyncItems(SourceLibraryId);
        ";
        historyCmd.ExecuteNonQuery();

        // Create UserSyncItems table (User Sync) - v11 schema: per-property records with hash-based image comparison
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
                SourceImageHash TEXT,
                LocalImageHash TEXT,
                SyncedImageHash TEXT,
                SourceImageSize INTEGER,
                LocalImageSize INTEGER,
                SyncedImageSize INTEGER,
                Status INTEGER NOT NULL DEFAULT 1,
                StatusDate TEXT NOT NULL,
                LastSyncTime TEXT,
                ErrorMessage TEXT,
                UNIQUE(SourceUserId, LocalUserId, PropertyCategory)
            );
            CREATE INDEX IF NOT EXISTS idx_user_sync_mapping ON UserSyncItems(SourceUserId, LocalUserId);
            CREATE INDEX IF NOT EXISTS idx_user_sync_status ON UserSyncItems(Status);
            CREATE INDEX IF NOT EXISTS idx_user_sync_category ON UserSyncItems(PropertyCategory);
        ";
        userCmd.ExecuteNonQuery();

        // Create PeopleSyncItems table (People Sync) - v17 schema: metadata blob
        using var peopleCmd = connection.CreateCommand();
        peopleCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS PeopleSyncItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PersonName TEXT NOT NULL,
                SourcePersonId TEXT,
                LocalPersonId TEXT,
                SourceOverview TEXT,
                LocalOverview TEXT,
                SourceProviderIds TEXT,
                LocalProviderIds TEXT,
                SourceMetadataValue TEXT,
                LocalMetadataValue TEXT,
                SourceImagesValue TEXT,
                LocalImagesValue TEXT,
                SourceImagesHash TEXT,
                SyncedImagesHash TEXT,
                Status INTEGER NOT NULL DEFAULT 1,
                StatusDate TEXT NOT NULL,
                LastSyncTime TEXT,
                ErrorMessage TEXT,
                UNIQUE(PersonName)
            );
            CREATE INDEX IF NOT EXISTS idx_people_sync_name ON PeopleSyncItems(PersonName);
            CREATE INDEX IF NOT EXISTS idx_people_sync_status ON PeopleSyncItems(Status);
        ";
        peopleCmd.ExecuteNonQuery();

        // Create MetadataSyncItems table (Metadata Sync) - v16 schema: includes folder item support
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
                SourceMetadataValue TEXT,
                LocalMetadataValue TEXT,
                SourceImagesValue TEXT,
                LocalImagesValue TEXT,
                SourceImagesHash TEXT,
                SyncedImagesHash TEXT,
                SourcePeopleValue TEXT,
                LocalPeopleValue TEXT,
                SourceStudiosValue TEXT,
                LocalStudiosValue TEXT,
                Status INTEGER NOT NULL DEFAULT 1,
                StatusDate TEXT NOT NULL,
                LastSyncTime TEXT,
                ErrorMessage TEXT,
                SourceETag TEXT,
                ItemType TEXT,
                IsFolder INTEGER NOT NULL DEFAULT 0,
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
    /// </summary>
    /// <param name="connection">Database connection.</param>
    /// <param name="fromVersion">Version to migrate from.</param>
    /// <param name="logger">Logger for migration messages.</param>
    /// <returns>True if migration succeeded, false if failed.</returns>
    public static bool MigrateSchema(SqliteConnection connection, int fromVersion, ILogger logger)
    {
        logger.LogInformation("Migrating database schema from v{From} to v{To}", fromVersion, CurrentSchemaVersion);

        using var transaction = connection.BeginTransaction();
        try
        {
            if (fromVersion < 2)
            {
                MigrateToV2(connection, transaction, logger);
            }

            if (fromVersion < 3)
            {
                MigrateToV3(connection, transaction, logger);
            }

            if (fromVersion < 4)
            {
                MigrateToV4(connection, transaction, logger);
            }

            if (fromVersion < 5)
            {
                MigrateToV5(logger);
            }

            if (fromVersion < 6)
            {
                MigrateToV6(connection, transaction, logger);
            }

            if (fromVersion < 7)
            {
                MigrateToV7(connection, transaction, logger);
            }

            if (fromVersion < 8)
            {
                MigrateToV8(connection, transaction, logger);
            }

            if (fromVersion < 9)
            {
                MigrateToV9(connection, transaction, logger);
            }

            if (fromVersion < 10)
            {
                MigrateToV10(connection, transaction, logger);
            }

            if (fromVersion < 11)
            {
                MigrateToV11(connection, transaction, logger);
            }

            if (fromVersion < 12)
            {
                MigrateToV12(connection, transaction, logger);
            }

            if (fromVersion < 13)
            {
                MigrateToV13(connection, transaction, logger);
            }

            if (fromVersion < 14)
            {
                MigrateToV14(connection, transaction, logger);
            }

            if (fromVersion < 15)
            {
                MigrateToV15(connection, transaction, logger);
            }

            if (fromVersion < 16)
            {
                MigrateToV16(connection, transaction, logger);
            }

            if (fromVersion < 17)
            {
                MigrateToV17(connection, transaction, logger);
            }

            SetSchemaVersion(connection, CurrentSchemaVersion);
            transaction.Commit();
            logger.LogInformation("Database migration completed successfully");
            return true;
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            logger.LogError(ex, "Database migration failed, rolled back changes");
            return false;
        }
    }

    /// <summary>
    /// Migration to v2: Add LastSyncTime, ErrorMessage, RetryCount columns and local_path index.
    /// </summary>
    private static void MigrateToV2(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        var alterStatements = new[]
        {
            "ALTER TABLE SyncItems ADD COLUMN LastSyncTime TEXT",
            "ALTER TABLE SyncItems ADD COLUMN ErrorMessage TEXT",
            "ALTER TABLE SyncItems ADD COLUMN RetryCount INTEGER DEFAULT 0"
        };

        foreach (var statement in alterStatements)
        {
            ExecuteAlterIfColumnMissing(connection, transaction, statement, logger);
        }

        using var idxCmd = connection.CreateCommand();
        idxCmd.Transaction = transaction;
        idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_local_path ON SyncItems(LocalPath)";
        idxCmd.ExecuteNonQuery();

        logger.LogInformation("Migration v2: Added LastSyncTime, ErrorMessage, RetryCount columns");
    }

    /// <summary>
    /// Migration to v3: Add SourceETag column for reliable change detection.
    /// </summary>
    private static void MigrateToV3(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        ExecuteAlterIfColumnMissing(
            connection,
            transaction,
            "ALTER TABLE SyncItems ADD COLUMN SourceETag TEXT",
            logger);

        logger.LogInformation("Migration v3: Added SourceETag column for change detection");
    }

    /// <summary>
    /// Migration to v4: Add PendingType column and migrate old PendingDeletion/PendingReplacement statuses.
    /// </summary>
    private static void MigrateToV4(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        ExecuteAlterIfColumnMissing(
            connection,
            transaction,
            "ALTER TABLE SyncItems ADD COLUMN PendingType INTEGER",
            logger);

        // Migrate old statuses: PendingDeletion (5) -> Pending (0) with PendingType.Deletion (2)
        using var migrateDeletionCmd = connection.CreateCommand();
        migrateDeletionCmd.Transaction = transaction;
        migrateDeletionCmd.CommandText = "UPDATE SyncItems SET Status = 0, PendingType = 2 WHERE Status = 5";
        var deletionCount = migrateDeletionCmd.ExecuteNonQuery();
        if (deletionCount > 0)
        {
            logger.LogInformation("Migrated {Count} PendingDeletion items to Pending with PendingType.Deletion", deletionCount);
        }

        // Migrate old statuses: PendingReplacement (6) -> Pending (0) with PendingType.Replacement (1)
        using var migrateReplacementCmd = connection.CreateCommand();
        migrateReplacementCmd.Transaction = transaction;
        migrateReplacementCmd.CommandText = "UPDATE SyncItems SET Status = 0, PendingType = 1 WHERE Status = 6";
        var replacementCount = migrateReplacementCmd.ExecuteNonQuery();
        if (replacementCount > 0)
        {
            logger.LogInformation("Migrated {Count} PendingReplacement items to Pending with PendingType.Replacement", replacementCount);
        }

        // Set PendingType.Download (0) for existing Pending items without a type
        using var migrateDownloadCmd = connection.CreateCommand();
        migrateDownloadCmd.Transaction = transaction;
        migrateDownloadCmd.CommandText = "UPDATE SyncItems SET PendingType = 0 WHERE Status = 0 AND PendingType IS NULL";
        migrateDownloadCmd.ExecuteNonQuery();

        logger.LogInformation("Migration v4: Added PendingType column and migrated legacy statuses");
    }

    /// <summary>
    /// Migration to v5: Status value 5 is now Deleting instead of old PendingDeletion.
    /// </summary>
    private static void MigrateToV5(ILogger logger)
    {
        // No schema changes, but status value 5 is now Deleting instead of old PendingDeletion
        // Old PendingDeletion items were already migrated to Pending+PendingType.Deletion in v4
        logger.LogInformation("Migration v5: Deleting status (5) is now available");
    }

    /// <summary>
    /// Migration to v6: Add CompanionFiles column to track downloaded companion files.
    /// </summary>
    private static void MigrateToV6(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        ExecuteAlterIfColumnMissing(
            connection,
            transaction,
            "ALTER TABLE SyncItems ADD COLUMN CompanionFiles TEXT",
            logger);

        logger.LogInformation("Migration v6: Added CompanionFiles column for tracking companion files");
    }

    /// <summary>
    /// Migration to v7: Add HistorySyncItems and UserSyncItems tables for modular sync.
    /// </summary>
    private static void MigrateToV7(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        // Create HistorySyncItems table
        using var historyCmd = connection.CreateCommand();
        historyCmd.Transaction = transaction;
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
                ErrorMessage TEXT,
                UNIQUE(SourceUserId, SourceItemId)
            );
            CREATE INDEX IF NOT EXISTS idx_history_user ON HistorySyncItems(SourceUserId, LocalUserId);
            CREATE INDEX IF NOT EXISTS idx_history_status ON HistorySyncItems(Status);
            CREATE INDEX IF NOT EXISTS idx_history_library ON HistorySyncItems(SourceLibraryId);
        ";
        historyCmd.ExecuteNonQuery();

        // Create UserSyncItems table (scaffolding for future use)
        using var userCmd = connection.CreateCommand();
        userCmd.Transaction = transaction;
        userCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS UserSyncItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceUserId TEXT NOT NULL,
                LocalUserId TEXT NOT NULL,
                PropertyName TEXT NOT NULL,
                SourceValue TEXT,
                LocalValue TEXT,
                MergedValue TEXT,
                Status INTEGER NOT NULL,
                StatusDate TEXT NOT NULL,
                LastSyncTime TEXT,
                ErrorMessage TEXT,
                UNIQUE(SourceUserId, PropertyName)
            );
            CREATE INDEX IF NOT EXISTS idx_user_sync_user ON UserSyncItems(SourceUserId, LocalUserId);
            CREATE INDEX IF NOT EXISTS idx_user_sync_status ON UserSyncItems(Status);
        ";
        userCmd.ExecuteNonQuery();

        logger.LogInformation("Migration v7: Added HistorySyncItems and UserSyncItems tables");
    }

    /// <summary>
    /// Migration to v8: Update UserSyncItems table with new schema for full user sync.
    /// </summary>
    private static void MigrateToV8(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        // Drop and recreate UserSyncItems table with new schema
        // The old table was scaffolding only, so no data migration needed
        using var dropCmd = connection.CreateCommand();
        dropCmd.Transaction = transaction;
        dropCmd.CommandText = "DROP TABLE IF EXISTS UserSyncItems";
        dropCmd.ExecuteNonQuery();

        // Create new UserSyncItems table with updated schema
        using var createCmd = connection.CreateCommand();
        createCmd.Transaction = transaction;
        createCmd.CommandText = @"
            CREATE TABLE UserSyncItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceUserId TEXT NOT NULL,
                LocalUserId TEXT NOT NULL,
                SourceUserName TEXT,
                LocalUserName TEXT,
                PropertyCategory TEXT NOT NULL,
                PropertyName TEXT NOT NULL,
                SourceValue TEXT,
                LocalValue TEXT,
                MergedValue TEXT,
                Status INTEGER NOT NULL DEFAULT 1,
                StatusDate TEXT NOT NULL,
                LastSyncTime TEXT,
                ErrorMessage TEXT,
                UNIQUE(SourceUserId, LocalUserId, PropertyCategory, PropertyName)
            );
            CREATE INDEX idx_user_sync_mapping ON UserSyncItems(SourceUserId, LocalUserId);
            CREATE INDEX idx_user_sync_status ON UserSyncItems(Status);
            CREATE INDEX idx_user_sync_category ON UserSyncItems(PropertyCategory);
        ";
        createCmd.ExecuteNonQuery();

        logger.LogInformation("Migration v8: Updated UserSyncItems table with full user sync schema");
    }

    /// <summary>
    /// Migration to v9: Restructure UserSyncItems to one record per user (aggregated settings).
    /// </summary>
    private static void MigrateToV9(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        // Drop old per-property UserSyncItems table and create new aggregated schema
        using var dropCmd = connection.CreateCommand();
        dropCmd.Transaction = transaction;
        dropCmd.CommandText = "DROP TABLE IF EXISTS UserSyncItems";
        dropCmd.ExecuteNonQuery();

        // Create new UserSyncItems table with one record per user mapping
        using var createCmd = connection.CreateCommand();
        createCmd.Transaction = transaction;
        createCmd.CommandText = @"
            CREATE TABLE UserSyncItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceUserId TEXT NOT NULL,
                LocalUserId TEXT NOT NULL,
                SourceUserName TEXT,
                LocalUserName TEXT,
                SourcePolicy TEXT,
                LocalPolicy TEXT,
                MergedPolicy TEXT,
                SourceConfiguration TEXT,
                LocalConfiguration TEXT,
                MergedConfiguration TEXT,
                SourceImageTag TEXT,
                SyncedImageTag TEXT,
                LocalHasImage INTEGER NOT NULL DEFAULT 0,
                SyncPolicy INTEGER NOT NULL DEFAULT 1,
                SyncConfiguration INTEGER NOT NULL DEFAULT 1,
                SyncProfileImage INTEGER NOT NULL DEFAULT 1,
                Status INTEGER NOT NULL DEFAULT 1,
                StatusDate TEXT NOT NULL,
                LastSyncTime TEXT,
                ErrorMessage TEXT,
                UNIQUE(SourceUserId, LocalUserId)
            );
            CREATE INDEX idx_user_sync_mapping ON UserSyncItems(SourceUserId, LocalUserId);
            CREATE INDEX idx_user_sync_status ON UserSyncItems(Status);
        ";
        createCmd.ExecuteNonQuery();

        logger.LogInformation("Migration v9: Restructured UserSyncItems to one record per user mapping");
    }

    /// <summary>
    /// Migration to v10: Restructure UserSyncItems to per-property records with size-based image comparison.
    /// </summary>
    private static void MigrateToV10(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        // Drop old aggregated UserSyncItems table and create per-property schema
        using var dropCmd = connection.CreateCommand();
        dropCmd.Transaction = transaction;
        dropCmd.CommandText = "DROP TABLE IF EXISTS UserSyncItems";
        dropCmd.ExecuteNonQuery();

        // Create new UserSyncItems table with per-property records and image size columns
        using var createCmd = connection.CreateCommand();
        createCmd.Transaction = transaction;
        createCmd.CommandText = @"
            CREATE TABLE UserSyncItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceUserId TEXT NOT NULL,
                LocalUserId TEXT NOT NULL,
                SourceUserName TEXT,
                LocalUserName TEXT,
                PropertyCategory TEXT NOT NULL,
                SourceValue TEXT,
                LocalValue TEXT,
                MergedValue TEXT,
                SourceImageSize INTEGER,
                LocalImageSize INTEGER,
                SyncedImageSize INTEGER,
                Status INTEGER NOT NULL DEFAULT 1,
                StatusDate TEXT NOT NULL,
                LastSyncTime TEXT,
                ErrorMessage TEXT,
                UNIQUE(SourceUserId, LocalUserId, PropertyCategory)
            );
            CREATE INDEX idx_user_sync_mapping ON UserSyncItems(SourceUserId, LocalUserId);
            CREATE INDEX idx_user_sync_status ON UserSyncItems(Status);
            CREATE INDEX idx_user_sync_category ON UserSyncItems(PropertyCategory);
        ";
        createCmd.ExecuteNonQuery();

        logger.LogInformation("Migration v10: UserSyncItems now uses per-property records with size-based image comparison");
    }

    /// <summary>
    /// Migration to v11: Add hash columns for more accurate profile image comparison.
    /// </summary>
    private static void MigrateToV11(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        // Add hash columns to UserSyncItems table
        var alterStatements = new[]
        {
            "ALTER TABLE UserSyncItems ADD COLUMN SourceImageHash TEXT",
            "ALTER TABLE UserSyncItems ADD COLUMN LocalImageHash TEXT",
            "ALTER TABLE UserSyncItems ADD COLUMN SyncedImageHash TEXT"
        };

        foreach (var statement in alterStatements)
        {
            ExecuteAlterIfColumnMissing(connection, transaction, statement, logger);
        }

        logger.LogInformation("Migration v11: Added hash columns for profile image comparison");
    }

    /// <summary>
    /// Migration to v12: Add MetadataSyncItems table for metadata synchronization.
    /// </summary>
    private static void MigrateToV12(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        // Create MetadataSyncItems table
        using var metadataCmd = connection.CreateCommand();
        metadataCmd.Transaction = transaction;
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
                PropertyCategory TEXT NOT NULL,
                SourceValue TEXT,
                LocalValue TEXT,
                MergedValue TEXT,
                SourceImagesHash TEXT,
                LocalImagesHash TEXT,
                SyncedImagesHash TEXT,
                Status INTEGER NOT NULL DEFAULT 1,
                StatusDate TEXT NOT NULL,
                LastSyncTime TEXT,
                ErrorMessage TEXT,
                UNIQUE(SourceLibraryId, SourceItemId, PropertyCategory)
            );
            CREATE INDEX IF NOT EXISTS idx_metadata_sync_item ON MetadataSyncItems(SourceItemId);
            CREATE INDEX IF NOT EXISTS idx_metadata_sync_status ON MetadataSyncItems(Status);
            CREATE INDEX IF NOT EXISTS idx_metadata_sync_library ON MetadataSyncItems(SourceLibraryId);
            CREATE INDEX IF NOT EXISTS idx_metadata_sync_category ON MetadataSyncItems(PropertyCategory);
        ";
        metadataCmd.ExecuteNonQuery();

        logger.LogInformation("Migration v12: Added MetadataSyncItems table for metadata synchronization");
    }

    /// <summary>
    /// Migration to v13: Restructure MetadataSyncItems from per-category to per-item records.
    /// </summary>
    private static void MigrateToV13(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        // Drop old per-category MetadataSyncItems table and create new per-item schema
        // Existing data will be lost but will be repopulated on next refresh task
        using var dropCmd = connection.CreateCommand();
        dropCmd.Transaction = transaction;
        dropCmd.CommandText = "DROP TABLE IF EXISTS MetadataSyncItems";
        dropCmd.ExecuteNonQuery();

        // Create new MetadataSyncItems table with one record per item (all categories combined)
        using var createCmd = connection.CreateCommand();
        createCmd.Transaction = transaction;
        createCmd.CommandText = @"
            CREATE TABLE MetadataSyncItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceLibraryId TEXT NOT NULL,
                LocalLibraryId TEXT NOT NULL,
                SourceItemId TEXT NOT NULL,
                LocalItemId TEXT,
                ItemName TEXT,
                SourcePath TEXT,
                LocalPath TEXT,
                SourceMetadataValue TEXT,
                LocalMetadataValue TEXT,
                SourceImagesValue TEXT,
                LocalImagesValue TEXT,
                SourceImagesHash TEXT,
                SyncedImagesHash TEXT,
                SourcePeopleValue TEXT,
                LocalPeopleValue TEXT,
                Status INTEGER NOT NULL DEFAULT 1,
                StatusDate TEXT NOT NULL,
                LastSyncTime TEXT,
                ErrorMessage TEXT,
                UNIQUE(SourceLibraryId, SourceItemId)
            );
            CREATE INDEX idx_metadata_sync_item ON MetadataSyncItems(SourceItemId);
            CREATE INDEX idx_metadata_sync_status ON MetadataSyncItems(Status);
            CREATE INDEX idx_metadata_sync_library ON MetadataSyncItems(SourceLibraryId);
        ";
        createCmd.ExecuteNonQuery();

        logger.LogInformation("Migration v13: MetadataSyncItems now uses one record per item with all categories combined");
    }

    /// <summary>
    /// Migration to v14: Add Studios columns to MetadataSyncItems table.
    /// </summary>
    private static void MigrateToV14(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        // Add Studios columns to MetadataSyncItems table
        var alterStatements = new[]
        {
            "ALTER TABLE MetadataSyncItems ADD COLUMN SourceStudiosValue TEXT",
            "ALTER TABLE MetadataSyncItems ADD COLUMN LocalStudiosValue TEXT"
        };

        foreach (var statement in alterStatements)
        {
            ExecuteAlterIfColumnMissing(connection, transaction, statement, logger);
        }

        logger.LogInformation("Migration v14: Added Studios columns to MetadataSyncItems table");
    }

    /// <summary>
    /// Migration to v15: Add SourceETag column to MetadataSyncItems for change detection.
    /// </summary>
    private static void MigrateToV15(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        ExecuteAlterIfColumnMissing(
            connection,
            transaction,
            "ALTER TABLE MetadataSyncItems ADD COLUMN SourceETag TEXT",
            logger);

        logger.LogInformation("Migration v15: Added SourceETag column to MetadataSyncItems for change detection");
    }

    /// <summary>
    /// Migration to v16: Add folder item support columns to MetadataSyncItems.
    /// </summary>
    private static void MigrateToV16(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        var alterStatements = new[]
        {
            "ALTER TABLE MetadataSyncItems ADD COLUMN ItemType TEXT",
            "ALTER TABLE MetadataSyncItems ADD COLUMN IsFolder INTEGER NOT NULL DEFAULT 0"
        };

        foreach (var statement in alterStatements)
        {
            ExecuteAlterIfColumnMissing(connection, transaction, statement, logger);
        }

        // Create PeopleSyncItems table
        using var peopleCmd = connection.CreateCommand();
        peopleCmd.Transaction = transaction;
        peopleCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS PeopleSyncItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PersonName TEXT NOT NULL,
                SourcePersonId TEXT,
                LocalPersonId TEXT,
                SourceOverview TEXT,
                LocalOverview TEXT,
                SourceProviderIds TEXT,
                LocalProviderIds TEXT,
                SourceImagesValue TEXT,
                LocalImagesValue TEXT,
                SourceImagesHash TEXT,
                SyncedImagesHash TEXT,
                Status INTEGER NOT NULL DEFAULT 1,
                StatusDate TEXT NOT NULL,
                LastSyncTime TEXT,
                ErrorMessage TEXT,
                UNIQUE(PersonName)
            );
            CREATE INDEX IF NOT EXISTS idx_people_sync_name ON PeopleSyncItems(PersonName);
            CREATE INDEX IF NOT EXISTS idx_people_sync_status ON PeopleSyncItems(Status);
        ";
        peopleCmd.ExecuteNonQuery();

        logger.LogInformation("Migration v16: Added ItemType, IsFolder columns and PeopleSyncItems table");
    }

    /// <summary>
    /// Migration to v17: Add SourceMetadataValue and LocalMetadataValue columns to PeopleSyncItems.
    /// </summary>
    private static void MigrateToV17(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        var alterStatements = new[]
        {
            "ALTER TABLE PeopleSyncItems ADD COLUMN SourceMetadataValue TEXT",
            "ALTER TABLE PeopleSyncItems ADD COLUMN LocalMetadataValue TEXT"
        };

        foreach (var statement in alterStatements)
        {
            ExecuteAlterIfColumnMissing(connection, transaction, statement, logger);
        }

        logger.LogInformation("Migration v17: Added SourceMetadataValue and LocalMetadataValue columns to PeopleSyncItems");
    }

    /// <summary>
    /// Executes an ALTER TABLE statement, ignoring "duplicate column" errors.
    /// </summary>
    private static void ExecuteAlterIfColumnMissing(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string statement,
        ILogger logger)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = statement;
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Column already exists, skipping: {Statement}", statement);
        }
    }
}
