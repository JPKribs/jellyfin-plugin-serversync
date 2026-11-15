using System;
using Microsoft.Data.Sqlite;

#pragma warning disable CA2100 // SQL commands use only internal constants and safe parameterized queries

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// The sync database's schema: every table as this release reads and writes it, and the version that
/// names it. There are no upgrade steps. A database written by a release before
/// <see cref="OldestUpgradableRelease"/> is refused, and that release is the one to pass through first.
/// </summary>
public static class SyncSchema
{
    /// <summary>
    /// The schema version. Raise it when the schema changes.
    /// </summary>
    public const int CurrentSchemaVersion = 36;

    /// <summary>
    /// The release whose database this one opens as it is. Anything older has to be started once on
    /// this release first, which is the last one that converts older databases and settings.
    /// </summary>
    public const string OldestUpgradableRelease = "12.4.13.0";

    /// <summary>
    /// Reads the schema version of the database under a data folder without changing the file.
    /// </summary>
    /// <param name="dataPath">Jellyfin's data folder.</param>
    /// <returns>The version, or null when there is no database or it cannot be read.</returns>
    public static int? StoredVersion(string dataPath)
    {
        var path = System.IO.Path.Combine(dataPath, "serversync", "sync.db");
        if (!System.IO.File.Exists(path))
        {
            return null;
        }

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>Whether a stored version is one an older release wrote and this release cannot read.</summary>
    /// <param name="storedVersion">The version read from the database, or null.</param>
    /// <returns><c>true</c> when the database needs <see cref="OldestUpgradableRelease"/> first.</returns>
    public static bool IsTooOld(int? storedVersion) => storedVersion is > 0 and < CurrentSchemaVersion;

    /// <summary>
    /// The oldest schema version whose code can still work with a database at
    /// <see cref="CurrentSchemaVersion"/>. It is written into the database, so an older build that finds a
    /// newer database can tell whether it may use it as is. Raise it only when a schema change alters or
    /// removes something older code relies on. One that only adds tables, nullable or defaulted
    /// columns, or indexes leaves it alone.
    /// </summary>
    public const int MinReaderVersion = 28;

    /// <summary>
    /// The oldest schema whose code knows links. A database that holds links names this as its oldest
    /// reader, so an older build sets it aside rather than taking a .strm for a copy whose size is wrong
    /// and downloading the media over it.
    /// </summary>
    public const int LinkReaderVersion = 32;

    /// <summary>
    /// The oldest schema whose code knows a kept copy going back to a link. A database holding one names
    /// this as its oldest reader, so an older build never takes that row for a copy still to download.
    /// </summary>
    public const int UnkeepReaderVersion = 33;

    /// <summary>The oldest reader a database with these contents allows: <see cref="LinkReaderVersion"/> once it holds a link.</summary>
    /// <param name="connection">Database connection.</param>
    /// <returns>The version.</returns>
    public static int MinReaderFor(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'SyncItems'";
        if (Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            return MinReaderVersion;
        }

        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM SyncItems WHERE StorageMode <> 0)";
        var reader = Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1 ? Math.Max(MinReaderVersion, LinkReaderVersion) : MinReaderVersion;
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM SyncItems WHERE StorageMode = 3)";
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1 ? Math.Max(reader, UnkeepReaderVersion) : reader;
    }

    /// <summary>
    /// Creates every table of the schema in an empty database.
    /// </summary>
    /// <param name="connection">Database connection.</param>
    public static void Create(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Tracks files to be downloaded/replaced/deleted on the local server.
        // Change detection uses Size only. StorageMode says whether a row is a
        // full copy or a .strm link, Kept why a copy in a Link mapping is kept,
        // and OriginServerId the server a copy first came from.
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
                StorageMode INTEGER NOT NULL DEFAULT 0,
                Kept INTEGER NOT NULL DEFAULT 0,
                OriginServerId TEXT,
                UNIQUE(SourceItemId)
            );
            CREATE INDEX IF NOT EXISTS idx_source_item ON SyncItems(SourceItemId);
            CREATE INDEX IF NOT EXISTS idx_status ON SyncItems(Status);
            CREATE INDEX IF NOT EXISTS idx_source_library ON SyncItems(SourceLibraryId);
            CREATE INDEX IF NOT EXISTS idx_local_path ON SyncItems(LocalPath);
        ";
        syncItemsCmd.ExecuteNonQuery();

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
            CREATE INDEX IF NOT EXISTS idx_history_server_local ON HistorySyncItems(ServerKey, LocalItemId);
        ";
        historyCmd.ExecuteNonQuery();

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

        // One row per item, four SyncableValue fields (Metadata, Images,
        // People, Studios). Hashes per category enable per-field
        // short-circuiting.
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

        // What this server has told its peers and what they have told it, the version each tracked
        // object carries, pairing secrets, scan watermarks, the playlists copied from peers, when each
        // failing row first failed, and the record of the files that left the library.
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
            CREATE INDEX IF NOT EXISTS idx_outbound_peer_state ON OutboundHints(PeerKey, State, Id);

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
                ParkedPath TEXT,
                UNIQUE(OriginServerId, Kind, Key)
            );
            CREATE INDEX IF NOT EXISTS idx_inbound_next ON InboundHints(NextAttempt);
            CREATE INDEX IF NOT EXISTS idx_inbound_origin ON InboundHints(OriginServerId);
            CREATE INDEX IF NOT EXISTS idx_inbound_parked_nocase ON InboundHints(ParkedPath COLLATE NOCASE);

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
            );

            CREATE TABLE IF NOT EXISTS PendingVersions (
                Kind INTEGER NOT NULL,
                Key TEXT NOT NULL,
                ServerId TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                ReadAt TEXT NOT NULL,
                PRIMARY KEY(Kind, Key)
            );

            CREATE TABLE IF NOT EXISTS ScanWatermarks (
                TaskKey TEXT NOT NULL,
                ServerKey TEXT NOT NULL,
                ScopeHash TEXT NOT NULL,
                ListedFrom TEXT NOT NULL,
                FullAt TEXT NOT NULL,
                PRIMARY KEY(TaskKey, ServerKey)
            );

            CREATE TABLE IF NOT EXISTS PlaylistCopies (
                PlaylistId TEXT NOT NULL PRIMARY KEY,
                RootId TEXT NOT NULL,
                ServerKey TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS SyncAttention (
                TableName TEXT NOT NULL,
                RowId INTEGER NOT NULL,
                FirstFailed TEXT NOT NULL,
                Reason TEXT,
                Reported INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(TableName, RowId)
            );

            CREATE TABLE IF NOT EXISTS RemovedItems (
                Seq INTEGER PRIMARY KEY AUTOINCREMENT,
                ItemId TEXT NOT NULL,
                RemovedAt TEXT NOT NULL,
                Path TEXT,
                OriginServerId TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_removed_items_at ON RemovedItems(RemovedAt);
            CREATE TABLE IF NOT EXISTS RemovedItemsLog (StartedAt TEXT NOT NULL);
            INSERT INTO RemovedItemsLog (StartedAt) SELECT @now WHERE NOT EXISTS (SELECT 1 FROM RemovedItemsLog);";
        cmd.Parameters.AddWithValue("@now", Utilities.UtcTime.Format(DateTime.UtcNow));
        cmd.ExecuteNonQuery();
    }
}
