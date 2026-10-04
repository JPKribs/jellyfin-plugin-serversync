using System;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

#pragma warning disable CA2100 // SQL commands use only internal constants and safe parameterized queries

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Handles database schema migrations for the sync database. Upgrades from
/// pre-v19 versions drop all tracking tables and recreate them. The next
/// refresh re-populates everything from source/local state.
/// </summary>
public static class DatabaseMigrationService
{
    /// <summary>
    /// Current schema version. Increment this when adding new migrations.
    /// </summary>
    public const int CurrentSchemaVersion = 28;

    /// <summary>
    /// The oldest schema version whose code can still work with a database at
    /// <see cref="CurrentSchemaVersion"/>. It is written into the database, so an older build that finds a
    /// newer database can tell whether it may use it as is. Raise it only when a migration changes or
    /// removes something older code relies on. A migration that only adds tables, nullable or defaulted
    /// columns, or indexes leaves it alone. Builds before v28 do not read it and still set a newer
    /// database aside.
    /// </summary>
    public const int MinReaderVersion = 28;

    /// <summary>
    /// Creates the initial database schema including all tables for the current version.
    /// </summary>
    /// <param name="connection">Database connection.</param>
    public static void CreateInitialSchema(SqliteConnection connection)
    {
        // ===== Content Sync (SyncItems) =====
        // Tracks files to be downloaded/replaced/deleted on the local server.
        // Change detection uses Size only (ETag was removed in v18, it was
        // unstable because Jellyfin's ETag changes when UserData changes.
        // SourceModifyDate was dropped in v19, it was never read by the
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
                ServerKey TEXT,
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
                NegotiatedIsPlayed INTEGER,
                NegotiatedPlayCount INTEGER,
                NegotiatedPlaybackPositionTicks INTEGER,
                NegotiatedLastPlayedDate TEXT,
                NegotiatedIsFavorite INTEGER,
                NegotiatedAt TEXT,
                Status INTEGER NOT NULL,
                StatusDate TEXT NOT NULL,
                LastSyncTime TEXT,
                Reason TEXT,
                RetryCount INTEGER NOT NULL DEFAULT 0,
                ServerKey TEXT,
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
        // the fast-path skip. SourceImageHash/LocalImageHash/SyncedImageHash
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
                RetryCount INTEGER NOT NULL DEFAULT 0,
                ServerKey TEXT,
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
                RetryCount INTEGER NOT NULL DEFAULT 0,
                ServerKey TEXT,
                UNIQUE(PersonName)
            );
            CREATE INDEX IF NOT EXISTS idx_people_sync_name ON PeopleSyncItems(PersonName);
            CREATE INDEX IF NOT EXISTS idx_people_sync_status ON PeopleSyncItems(Status);
        ";
        peopleCmd.ExecuteNonQuery();

        // ===== Metadata Sync (MetadataSyncItems) =====
        // One row per item, four SyncableValue fields (Metadata, Images,
        // People, Studios). Hashes per category enable per-field
        // short-circuiting. SourceETag removed, we use SourceMetadataHash
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
                RetryCount INTEGER NOT NULL DEFAULT 0,
                ServerKey TEXT,
                UNIQUE(SourceLibraryId, SourceItemId)
            );
            CREATE INDEX IF NOT EXISTS idx_metadata_sync_item ON MetadataSyncItems(SourceItemId);
            CREATE INDEX IF NOT EXISTS idx_metadata_sync_status ON MetadataSyncItems(Status);
            CREATE INDEX IF NOT EXISTS idx_metadata_sync_library ON MetadataSyncItems(SourceLibraryId);
        ";
        metadataCmd.ExecuteNonQuery();

        CreateQueueTables(connection);
    }

    /// <summary>
    /// Creates the tables behind change hints: what this server has told its peers, what its peers
    /// have told it, and the version each tracked object carries. Idempotent.
    /// </summary>
    /// <param name="connection">Database connection.</param>
    public static void CreateQueueTables(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS OutboundHints (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                HintId TEXT NOT NULL,
                PeerKey TEXT NOT NULL,
                Kind INTEGER NOT NULL,
                Key TEXT NOT NULL,
                ItemPath TEXT,
                ItemId TEXT,
                UserId TEXT,
                UserName TEXT,
                ItemType TEXT,
                VersionServerId TEXT NOT NULL,
                VersionTimestamp TEXT NOT NULL,
                Recorded INTEGER NOT NULL DEFAULT 1,
                State INTEGER NOT NULL,
                Attempts INTEGER NOT NULL DEFAULT 0,
                NextAttempt TEXT NOT NULL,
                SentAt TEXT,
                LastError TEXT,
                CreatedAt TEXT NOT NULL,
                UNIQUE(PeerKey, Kind, Key)
            );
            CREATE INDEX IF NOT EXISTS idx_outbound_state ON OutboundHints(State, NextAttempt);
            CREATE INDEX IF NOT EXISTS idx_outbound_hint ON OutboundHints(HintId);

            CREATE TABLE IF NOT EXISTS InboundHints (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                HintId TEXT NOT NULL,
                OriginServerId TEXT NOT NULL,
                Kind INTEGER NOT NULL,
                Key TEXT NOT NULL,
                ItemPath TEXT,
                ItemId TEXT,
                UserId TEXT,
                UserName TEXT,
                ItemType TEXT,
                VersionServerId TEXT NOT NULL,
                VersionTimestamp TEXT NOT NULL,
                Recorded INTEGER NOT NULL DEFAULT 1,
                ReceivedAt TEXT NOT NULL,
                Attempts INTEGER NOT NULL DEFAULT 0,
                NextAttempt TEXT NOT NULL,
                LastError TEXT,
                UNIQUE(OriginServerId, Kind, Key)
            );
            CREATE INDEX IF NOT EXISTS idx_inbound_next ON InboundHints(NextAttempt);

            CREATE TABLE IF NOT EXISTS ObjectVersions (
                Kind INTEGER NOT NULL,
                Key TEXT NOT NULL,
                ServerId TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                PRIMARY KEY(Kind, Key)
            );

            CREATE TABLE IF NOT EXISTS PeerPairings (
                PeerKey TEXT NOT NULL PRIMARY KEY,
                InboundSecret TEXT,
                OutboundSecret TEXT,
                InboundRefusedAt TEXT,
                UpdatedAt TEXT NOT NULL
            );";
        cmd.ExecuteNonQuery();
        CreateV28Additions(connection);
    }

    /// <summary>
    /// The pending versions a scan read alongside the values it queued, and the indexes the hint queues
    /// and the history negotiation look rows up by. Idempotent, so a fresh database and an upgrade share it.
    /// </summary>
    /// <param name="connection">Database connection.</param>
    public static void CreateV28Additions(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS PendingVersions (
                Kind INTEGER NOT NULL,
                Key TEXT NOT NULL,
                ServerId TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                ReadAt TEXT NOT NULL,
                PRIMARY KEY(Kind, Key)
            );
            CREATE INDEX IF NOT EXISTS idx_outbound_peer_state ON OutboundHints(PeerKey, State, Id);
            CREATE INDEX IF NOT EXISTS idx_inbound_origin ON InboundHints(OriginServerId);";
        cmd.ExecuteNonQuery();

        // The history table is created with the rest of the schema. The queue tables can be created on
        // their own, so its index is added only when the table and its server column exist.
        using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('HistorySyncItems') WHERE name IN ('ServerKey', 'LocalItemId')";
        if (Convert.ToInt32(probe.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 2)
        {
            using var index = connection.CreateCommand();
            index.CommandText = "CREATE INDEX IF NOT EXISTS idx_history_server_local ON HistorySyncItems(ServerKey, LocalItemId)";
            index.ExecuteNonQuery();
        }
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
    /// Marks the database as this build's schema: the version, and the oldest version that can still read
    /// it. Called whenever this build creates or upgrades the database.
    /// </summary>
    /// <param name="connection">Database connection.</param>
    public static void StampSchema(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS SchemaInfo (Key TEXT NOT NULL PRIMARY KEY, Value INTEGER NOT NULL);
            INSERT INTO SchemaInfo (Key, Value) VALUES ('MinReaderVersion', @min)
            ON CONFLICT(Key) DO UPDATE SET Value = @min;";
        command.Parameters.AddWithValue("@min", MinReaderVersion);
        command.ExecuteNonQuery();
        SetSchemaVersion(connection, CurrentSchemaVersion);
    }

    /// <summary>
    /// Reads the oldest schema version the database says can still read it, or null when the database
    /// was written by a build that did not record one.
    /// </summary>
    /// <param name="connection">Database connection.</param>
    /// <returns>The version, or null.</returns>
    public static int? GetMinReaderVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'SchemaInfo'";
        if (Convert.ToInt32(probe.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM SchemaInfo WHERE Key = 'MinReaderVersion'";
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
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
    /// Any version below 19 is a hard reset. Its tables are dropped and recreated.
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
            // riskier than rebuilding from source, the next refresh
            // repopulates everything cleanly.
            if (fromVersion < 19)
            {
                logger.LogWarning(
                    "Schema upgrade to v{Target}: dropping all sync tracking tables (was v{From}). Sync tracking data will be lost. The next refresh will repopulate everything from source/local state.",
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
            // row. Matching rows get re-MarkSynced, diverging rows requeue.
            // Status is left alone so Ignored overrides survive.
            if (fromVersion >= 19 && fromVersion < 20)
            {
                logger.LogWarning(
                    "Schema upgrade to v20: clearing poisoned SyncedHash columns left over from 10.11.54. The next Refresh will re-evaluate every row against the source via comparator (no data loss. Ignored overrides preserved).");

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
            // JsonBlobComparator SHA256 (64 hex), the next Refresh re-seeds
            // them via the comparator path.
            if (fromVersion >= 19 && fromVersion < 21)
            {
                logger.LogWarning(
                    "Schema upgrade to v21: adding SourceStateHash/SyncedStateHash columns to HistorySyncItems and clearing stale SyncedValueHash on UserSyncItems for the SyncableValue migration.");

                using var v21Transaction = connection.BeginTransaction();

                foreach (var col in new[] { "SourceStateHash", "SyncedStateHash" })
                {
                    AddColumnIfMissing(connection, v21Transaction, "HistorySyncItems", $"{col} TEXT");
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

            // v22: give every module the retry ceiling Content already had.
            // Without a RetryCount the refresh re-queued an Errored row on
            // every run and the sync re-applied it, so a row that could never
            // converge churned forever.
            if (fromVersion >= 19 && fromVersion < 22)
            {
                logger.LogWarning(
                    "Schema upgrade to v22: adding RetryCount to the History, User, People and Metadata tables so permanently failing rows stop being retried without bound.");

                using var v22Transaction = connection.BeginTransaction();

                foreach (var table in new[]
                {
                    "HistorySyncItems",
                    "UserSyncItems",
                    "PeopleSyncItems",
                    "MetadataSyncItems"
                })
                {
                    AddColumnIfMissing(connection, v22Transaction, table, "RetryCount INTEGER NOT NULL DEFAULT 0");
                }

                v22Transaction.Commit();
            }

            // v23: history rows remember the state both servers last agreed on, so a sync that
            // negotiates with the source can merge three ways instead of letting the source always win.
            if (fromVersion >= 19 && fromVersion < 23)
            {
                logger.LogWarning(
                    "Schema upgrade to v23: adding the negotiated base columns to HistorySyncItems for two way history sync.");

                using var v23Transaction = connection.BeginTransaction();

                foreach (var (col, type) in new[]
                {
                    ("NegotiatedIsPlayed", "INTEGER"),
                    ("NegotiatedPlayCount", "INTEGER"),
                    ("NegotiatedPlaybackPositionTicks", "INTEGER"),
                    ("NegotiatedLastPlayedDate", "TEXT"),
                    ("NegotiatedIsFavorite", "INTEGER"),
                    ("NegotiatedAt", "TEXT")
                })
                {
                    AddColumnIfMissing(connection, v23Transaction, "HistorySyncItems", $"{col} {type}");
                }

                v23Transaction.Commit();
            }

            // v24: every row remembers which configured server it came from, so several scan servers can
            // share one table and the apply tasks know which peer to talk to for each row.
            if (fromVersion >= 19 && fromVersion < 24)
            {
                logger.LogWarning("Schema upgrade to v24: adding ServerKey to every sync table for multi server scanning.");

                using var v24Transaction = connection.BeginTransaction();

                foreach (var table in new[]
                {
                    "SyncItems",
                    "HistorySyncItems",
                    "UserSyncItems",
                    "PeopleSyncItems",
                    "MetadataSyncItems"
                })
                {
                    AddColumnIfMissing(connection, v24Transaction, table, "ServerKey TEXT");
                }

                v24Transaction.Commit();
            }

            // v25: queues for change hints between peers and the version each object carries. New
            // tables only, so every older schema simply gains them.
            if (fromVersion < 25)
            {
                logger.LogInformation("Schema upgrade to v25: adding hint queues and object versions.");
                CreateQueueTables(connection);
            }

            // v26: a hint says whether it carries a hand made edit or a provider's work.
            if (fromVersion >= 25 && fromVersion < 26)
            {
                logger.LogInformation("Schema upgrade to v26: marking hints as hand made or provider work.");
                using var v26Transaction = connection.BeginTransaction();
                foreach (var table in new[] { "OutboundHints", "InboundHints" })
                {
                    AddColumnIfMissing(connection, v26Transaction, table, "Recorded INTEGER NOT NULL DEFAULT 1");
                }

                v26Transaction.Commit();
            }

            // v27: pairing secrets that tie a peer's requests to its server entry, and the item type a
            // hint concerns so the queue view can show it in the right shape.
            if (fromVersion < 27)
            {
                logger.LogInformation("Schema upgrade to v27: adding peer pairings and hint item types.");
                using var v27Transaction = connection.BeginTransaction();
                using (var pairings = connection.CreateCommand())
                {
                    pairings.Transaction = v27Transaction;
                    pairings.CommandText = @"
                        CREATE TABLE IF NOT EXISTS PeerPairings (
                            PeerKey TEXT NOT NULL PRIMARY KEY,
                            InboundSecret TEXT,
                            OutboundSecret TEXT,
                            InboundRefusedAt TEXT,
                            UpdatedAt TEXT NOT NULL
                        )";
                    pairings.ExecuteNonQuery();
                }

                foreach (var table in new[] { "OutboundHints", "InboundHints" })
                {
                    AddColumnIfMissing(connection, v27Transaction, table, "ItemType TEXT");
                }

                v27Transaction.Commit();
            }

            // v28: pending versions read by the scan, and indexes for the per peer queue counts and the
            // history negotiation's lookups by local item.
            if (fromVersion < 28)
            {
                logger.LogInformation("Schema upgrade to v28: adding pending versions and queue indexes.");
                CreateV28Additions(connection);
            }

            StampSchema(connection);
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

    /// <summary>
    /// Adds a column inside a migration transaction, doing nothing when the column is already there.
    /// A crashed upgrade can leave some columns applied, and a fresh database already has them, so a
    /// duplicate column is not a failure.
    /// </summary>
    /// <param name="connection">Database connection.</param>
    /// <param name="transaction">The migration step's transaction.</param>
    /// <param name="table">The table, an internal constant.</param>
    /// <param name="columnDefinition">The column name and type, an internal constant.</param>
    private static void AddColumnIfMissing(SqliteConnection connection, SqliteTransaction transaction, string table, string columnDefinition)
    {
        using var addCol = connection.CreateCommand();
        addCol.Transaction = transaction;
        addCol.CommandText = $"ALTER TABLE {table} ADD COLUMN {columnDefinition}";
        try
        {
            addCol.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Already applied, nothing to do.
        }
    }
}
