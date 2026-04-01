using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

#pragma warning disable CA2100 // SQL commands use only internal constants and safe parameterized queries

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// SyncDatabase — Metadata Sync methods.
/// </summary>
public partial class SyncDatabase
{
    // ============================================
    // Metadata Sync Methods
    // ============================================

    /// <summary>
    /// Gets a metadata sync item by its ID.
    /// </summary>
    public MetadataSyncItem? GetMetadataSyncItemById(long id)
    {
        return ExecuteRead(() =>
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM MetadataSyncItems WHERE Id = @id";
            command.Parameters.AddWithValue("@id", id);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadMetadataSyncItem(reader) : null;
        }, fallbackValue: null);
    }

    /// <summary>
    /// Gets a metadata sync item by source library ID and source item ID.
    /// </summary>
    public MetadataSyncItem? GetMetadataSyncItem(string sourceLibraryId, string sourceItemId)
    {
        return ExecuteRead(() =>
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = @"
                SELECT * FROM MetadataSyncItems
                WHERE SourceLibraryId = @sourceLibraryId
                  AND SourceItemId = @sourceItemId";
            command.Parameters.AddWithValue("@sourceLibraryId", sourceLibraryId);
            command.Parameters.AddWithValue("@sourceItemId", sourceItemId);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadMetadataSyncItem(reader) : null;
        }, fallbackValue: null);
    }

    /// <summary>
    /// Gets a metadata sync item for a specific source item (one record per item).
    /// </summary>
    public MetadataSyncItem? GetMetadataSyncItemBySourceItem(string sourceItemId)
    {
        return ExecuteRead(() =>
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM MetadataSyncItems WHERE SourceItemId = @sourceItemId";
            command.Parameters.AddWithValue("@sourceItemId", sourceItemId);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadMetadataSyncItem(reader) : null;
        }, fallbackValue: null);
    }

    /// <summary>
    /// Gets all metadata sync items for a specific library.
    /// </summary>
    public List<MetadataSyncItem> GetMetadataSyncItemsByLibrary(string sourceLibraryId)
    {
        return ExecuteRead(() =>
        {
            var items = new List<MetadataSyncItem>();
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM MetadataSyncItems WHERE SourceLibraryId = @sourceLibraryId";
            command.Parameters.AddWithValue("@sourceLibraryId", sourceLibraryId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadMetadataSyncItem(reader));
            }

            return items;
        }, fallbackValue: new List<MetadataSyncItem>());
    }

    /// <summary>
    /// Gets all metadata sync items with a specific status.
    /// </summary>
    public List<MetadataSyncItem> GetMetadataSyncItemsByStatus(BaseSyncStatus status)
    {
        return ExecuteRead(() =>
        {
            var items = new List<MetadataSyncItem>();
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM MetadataSyncItems WHERE Status = @status";
            command.Parameters.AddWithValue("@status", (int)status);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadMetadataSyncItem(reader));
            }

            return items;
        }, fallbackValue: new List<MetadataSyncItem>());
    }

    /// <summary>
    /// Searches metadata sync items with pagination support.
    /// </summary>
    public (List<MetadataSyncItem> Items, int TotalCount) SearchMetadataSyncItemsPaginated(
        string? searchTerm = null,
        BaseSyncStatus? status = null,
        string? sourceLibraryId = null,
        int skip = 0,
        int take = 50)
    {
        return ExecuteRead(() =>
        {
            var conditions = new List<string>();

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            conditions.Add("(ItemName LIKE @searchTerm OR SourcePath LIKE @searchTerm)");
        }

        if (status.HasValue)
        {
            conditions.Add("Status = @status");
        }

        if (!string.IsNullOrWhiteSpace(sourceLibraryId))
        {
            conditions.Add("SourceLibraryId = @sourceLibraryId");
        }

        var whereClause = conditions.Count > 0
            ? $"WHERE {string.Join(" AND ", conditions)}"
            : string.Empty;

        // Get total count
        int totalCount;
        using (var countCommand = _connection!.CreateCommand())
        {
            countCommand.CommandText = $"SELECT COUNT(*) FROM MetadataSyncItems {whereClause}";

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                countCommand.Parameters.AddWithValue("@searchTerm", $"%{searchTerm}%");
            }

            if (status.HasValue)
            {
                countCommand.Parameters.AddWithValue("@status", (int)status.Value);
            }

            if (!string.IsNullOrWhiteSpace(sourceLibraryId))
            {
                countCommand.Parameters.AddWithValue("@sourceLibraryId", sourceLibraryId);
            }

            totalCount = Convert.ToInt32(countCommand.ExecuteScalar());
        }

        // Get paginated data
        var items = new List<MetadataSyncItem>();
        using (var dataCommand = _connection!.CreateCommand())
        {
            dataCommand.CommandText = $@"
                SELECT * FROM MetadataSyncItems
                {whereClause}
                ORDER BY
                    CASE Status
                        WHEN 3 THEN 0  -- Errored first
                        WHEN 1 THEN 1  -- Queued second
                        WHEN 4 THEN 2  -- Ignored third
                        WHEN 2 THEN 3  -- Synced last
                        ELSE 4
                    END,
                    ItemName ASC
                LIMIT @take OFFSET @skip";

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                dataCommand.Parameters.AddWithValue("@searchTerm", $"%{searchTerm}%");
            }

            if (status.HasValue)
            {
                dataCommand.Parameters.AddWithValue("@status", (int)status.Value);
            }

            if (!string.IsNullOrWhiteSpace(sourceLibraryId))
            {
                dataCommand.Parameters.AddWithValue("@sourceLibraryId", sourceLibraryId);
            }

            dataCommand.Parameters.AddWithValue("@take", take);
            dataCommand.Parameters.AddWithValue("@skip", skip);

            using var reader = dataCommand.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadMetadataSyncItem(reader));
            }
        }

            return (items, totalCount);
        }, fallbackValue: (new List<MetadataSyncItem>(), 0));
    }

    /// <summary>
    /// Gets metadata sync status counts.
    /// </summary>
    /// <summary>
    /// Gets all non-empty SourcePeopleValue JSON strings from the metadata sync table.
    /// Lightweight query that avoids loading full MetadataSyncItem objects.
    /// </summary>
    public List<string> GetAllSourcePeopleValues()
    {
        return ExecuteRead(() =>
        {
            var values = new List<string>();
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT SourcePeopleValue FROM MetadataSyncItems WHERE SourcePeopleValue IS NOT NULL AND SourcePeopleValue != '[]'";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var val = reader.GetString(0);
                if (!string.IsNullOrEmpty(val))
                {
                    values.Add(val);
                }
            }

            return values;
        }, fallbackValue: new List<string>());
    }

    public Dictionary<BaseSyncStatus, int> GetMetadataSyncStatusCounts()
    {
        return ExecuteRead(() =>
        {
            var counts = new Dictionary<BaseSyncStatus, int>();
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT Status, COUNT(*) as Count FROM MetadataSyncItems GROUP BY Status";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var syncStatus = (BaseSyncStatus)reader.GetInt32(0);
                var count = reader.GetInt32(1);
                counts[syncStatus] = count;
            }

            return counts;
        }, fallbackValue: new Dictionary<BaseSyncStatus, int>());
    }

    /// <summary>
    /// Upserts a metadata sync item.
    /// </summary>
    public void UpsertMetadataSyncItem(MetadataSyncItem item, SqliteTransaction? transaction = null)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            using var command = _connection!.CreateCommand();
            command.Transaction = transaction;

            // Insert or update metadata sync item (one record per item with all categories)
            // Preserve Ignored status - don't overwrite if user explicitly ignored the item
            command.CommandText = @"
                INSERT INTO MetadataSyncItems (
                    SourceLibraryId, LocalLibraryId, SourceItemId, LocalItemId,
                    ItemName, SourcePath, LocalPath,
                    SourceMetadataValue, LocalMetadataValue,
                    SourceImagesValue, LocalImagesValue, SourceImagesHash, SyncedImagesHash,
                    SourcePeopleValue, LocalPeopleValue,
                    SourceStudiosValue, LocalStudiosValue,
                    Status, StatusDate, LastSyncTime, ErrorMessage, SourceETag,
                    ItemType, IsFolder
                ) VALUES (
                    @sourceLibraryId, @localLibraryId, @sourceItemId, @localItemId,
                    @itemName, @sourcePath, @localPath,
                    @sourceMetadataValue, @localMetadataValue,
                    @sourceImagesValue, @localImagesValue, @sourceImagesHash, @syncedImagesHash,
                    @sourcePeopleValue, @localPeopleValue,
                    @sourceStudiosValue, @localStudiosValue,
                    @status, @statusDate, @lastSyncTime, @errorMessage, @sourceETag,
                    @itemType, @isFolder
                )
                ON CONFLICT(SourceLibraryId, SourceItemId) DO UPDATE SET
                    LocalLibraryId = @localLibraryId,
                    LocalItemId = @localItemId,
                    ItemName = @itemName,
                    SourcePath = @sourcePath,
                    LocalPath = @localPath,
                    SourceMetadataValue = @sourceMetadataValue,
                    LocalMetadataValue = @localMetadataValue,
                    SourceImagesValue = @sourceImagesValue,
                    LocalImagesValue = @localImagesValue,
                    SourceImagesHash = @sourceImagesHash,
                    SyncedImagesHash = CASE
                        WHEN @syncedImagesHash IS NOT NULL THEN @syncedImagesHash
                        ELSE MetadataSyncItems.SyncedImagesHash
                    END,
                    SourcePeopleValue = @sourcePeopleValue,
                    LocalPeopleValue = @localPeopleValue,
                    SourceStudiosValue = @sourceStudiosValue,
                    LocalStudiosValue = @localStudiosValue,
                    Status = CASE
                        WHEN MetadataSyncItems.Status = @ignoredStatus THEN @ignoredStatus
                        ELSE @status
                    END,
                    StatusDate = CASE
                        WHEN MetadataSyncItems.Status = @ignoredStatus THEN MetadataSyncItems.StatusDate
                        ELSE @statusDate
                    END,
                    LastSyncTime = CASE
                        WHEN MetadataSyncItems.Status = @ignoredStatus THEN MetadataSyncItems.LastSyncTime
                        ELSE @lastSyncTime
                    END,
                    ErrorMessage = CASE
                        WHEN MetadataSyncItems.Status = @ignoredStatus THEN MetadataSyncItems.ErrorMessage
                        ELSE @errorMessage
                    END,
                    SourceETag = @sourceETag,
                    ItemType = @itemType,
                    IsFolder = @isFolder
            ";

            command.Parameters.AddWithValue("@sourceLibraryId", item.SourceLibraryId);
            command.Parameters.AddWithValue("@localLibraryId", item.LocalLibraryId);
            command.Parameters.AddWithValue("@sourceItemId", item.SourceItemId);
            command.Parameters.AddWithValue("@localItemId", item.LocalItemId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@itemName", item.ItemName ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourcePath", item.SourcePath ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localPath", item.LocalPath ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourceMetadataValue", item.SourceMetadataValue ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localMetadataValue", item.LocalMetadataValue ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourceImagesValue", item.SourceImagesValue ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localImagesValue", item.LocalImagesValue ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourceImagesHash", item.SourceImagesHash ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@syncedImagesHash", item.SyncedImagesHash ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourcePeopleValue", item.SourcePeopleValue ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localPeopleValue", item.LocalPeopleValue ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourceStudiosValue", item.SourceStudiosValue ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localStudiosValue", item.LocalStudiosValue ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@status", (int)item.Status);
            command.Parameters.AddWithValue("@statusDate", item.StatusDate.ToString("o"));
            command.Parameters.AddWithValue("@lastSyncTime", item.LastSyncTime?.ToString("o") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@errorMessage", item.ErrorMessage ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@ignoredStatus", (int)BaseSyncStatus.Ignored);
            command.Parameters.AddWithValue("@sourceETag", item.SourceETag ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@itemType", item.ItemType ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@isFolder", item.IsFolder ? 1 : 0);

            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Updates the status of a metadata sync item by database ID.
    /// </summary>
    public void UpdateMetadataSyncItemStatusById(long id, BaseSyncStatus status, string? errorMessage = null)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            using var command = _connection!.CreateCommand();

            if (status == BaseSyncStatus.Synced)
            {
                command.CommandText = @"
                    UPDATE MetadataSyncItems
                    SET Status = @status,
                        StatusDate = @statusDate,
                        LastSyncTime = @statusDate,
                        ErrorMessage = NULL
                    WHERE Id = @id";
            }
            else if (status == BaseSyncStatus.Queued)
            {
                // Clear SyncedImagesHash so HasImagesChanges returns true and images are re-synced
                command.CommandText = @"
                    UPDATE MetadataSyncItems
                    SET Status = @status,
                        StatusDate = @statusDate,
                        ErrorMessage = @errorMessage,
                        SyncedImagesHash = NULL
                    WHERE Id = @id";
                command.Parameters.AddWithValue("@errorMessage", errorMessage ?? (object)DBNull.Value);
            }
            else
            {
                command.CommandText = @"
                    UPDATE MetadataSyncItems
                    SET Status = @status,
                        StatusDate = @statusDate,
                        ErrorMessage = @errorMessage
                    WHERE Id = @id";
                command.Parameters.AddWithValue("@errorMessage", errorMessage ?? (object)DBNull.Value);
            }

            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@status", (int)status);
            command.Parameters.AddWithValue("@statusDate", DateTime.UtcNow.ToString("o"));

            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Batch updates status for multiple metadata sync items by their database IDs.
    /// </summary>
    public int BatchUpdateMetadataSyncItemStatusByIds(IEnumerable<long> ids, BaseSyncStatus status)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            var count = 0;
            using var transaction = _connection!.BeginTransaction();
            try
            {
                using var command = _connection!.CreateCommand();
                command.Transaction = transaction;
                // When queueing, clear SyncedImagesHash so images are re-synced
                if (status == BaseSyncStatus.Queued)
                {
                    command.CommandText = @"
                        UPDATE MetadataSyncItems
                        SET Status = @status,
                            StatusDate = @statusDate,
                            SyncedImagesHash = NULL
                        WHERE Id = @id";
                }
                else
                {
                    command.CommandText = @"
                        UPDATE MetadataSyncItems
                        SET Status = @status,
                            StatusDate = @statusDate
                        WHERE Id = @id";
                }

                var idParam = new SqliteParameter("@id", 0L);
                var statusDateParam = new SqliteParameter("@statusDate", DateTime.UtcNow.ToString("o"));
                command.Parameters.Add(idParam);
                command.Parameters.Add(new SqliteParameter("@status", (int)status));
                command.Parameters.Add(statusDateParam);

                foreach (var id in ids)
                {
                    idParam.Value = id;
                    statusDateParam.Value = DateTime.UtcNow.ToString("o");
                    count += command.ExecuteNonQuery();
                }

                transaction.Commit();
                return count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch update metadata sync status by IDs failed after {Count} items, rolling back", count);
                transaction.Rollback();
                throw;
            }
        }
    }

    /// <summary>
    /// Deletes all metadata sync items for a specific source item.
    /// </summary>
    public int DeleteMetadataSyncItemsBySourceItem(string sourceItemId)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            using var command = _connection!.CreateCommand();
            command.CommandText = "DELETE FROM MetadataSyncItems WHERE SourceItemId = @sourceItemId";
            command.Parameters.AddWithValue("@sourceItemId", sourceItemId);

            return command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Resets the metadata sync database by clearing all items.
    /// Falls back to a full database recreation if the database is readonly or corrupted.
    /// </summary>
    public void ResetMetadataSyncDatabase()
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            _logger.LogWarning("Resetting metadata sync database - all metadata sync tracking data will be lost");

            try
            {
                EnsureConnection();
                using var command = _connection!.CreateCommand();
                command.CommandText = "DELETE FROM MetadataSyncItems WHERE 1=1";
                var deleted = command.ExecuteNonQuery();
                _logger.LogInformation("Metadata sync database has been reset, {Count} items removed", deleted);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                _logger.LogInformation("Metadata sync table does not exist yet, nothing to reset");
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 8)
            {
                _logger.LogWarning(ex, "Database is readonly, falling back to full database recreation");
                RecreateDatabase();
            }
        }
    }

    /// <summary>
    /// Reads a MetadataSyncItem from the database reader.
    /// </summary>
    private static MetadataSyncItem ReadMetadataSyncItem(SqliteDataReader reader)
    {
        var item = new MetadataSyncItem
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            SourceLibraryId = reader.GetString(reader.GetOrdinal("SourceLibraryId")),
            LocalLibraryId = reader.GetString(reader.GetOrdinal("LocalLibraryId")),
            SourceItemId = reader.GetString(reader.GetOrdinal("SourceItemId")),
            Status = (BaseSyncStatus)reader.GetInt32(reader.GetOrdinal("Status")),
            StatusDate = ParseDateTimeSafe(reader.GetString(reader.GetOrdinal("StatusDate")))
        };

        // Optional string fields
        var localItemIdOrdinal = reader.GetOrdinal("LocalItemId");
        if (!reader.IsDBNull(localItemIdOrdinal))
        {
            item.LocalItemId = reader.GetString(localItemIdOrdinal);
        }

        var itemNameOrdinal = reader.GetOrdinal("ItemName");
        if (!reader.IsDBNull(itemNameOrdinal))
        {
            item.ItemName = reader.GetString(itemNameOrdinal);
        }

        var sourcePathOrdinal = reader.GetOrdinal("SourcePath");
        if (!reader.IsDBNull(sourcePathOrdinal))
        {
            item.SourcePath = reader.GetString(sourcePathOrdinal);
        }

        var localPathOrdinal = reader.GetOrdinal("LocalPath");
        if (!reader.IsDBNull(localPathOrdinal))
        {
            item.LocalPath = reader.GetString(localPathOrdinal);
        }

        // Metadata category fields
        var sourceMetadataOrdinal = TryGetOrdinal(reader, "SourceMetadataValue");
        if (sourceMetadataOrdinal >= 0 && !reader.IsDBNull(sourceMetadataOrdinal))
        {
            item.SourceMetadataValue = reader.GetString(sourceMetadataOrdinal);
        }

        var localMetadataOrdinal = TryGetOrdinal(reader, "LocalMetadataValue");
        if (localMetadataOrdinal >= 0 && !reader.IsDBNull(localMetadataOrdinal))
        {
            item.LocalMetadataValue = reader.GetString(localMetadataOrdinal);
        }

        // Images category fields
        var sourceImagesOrdinal = TryGetOrdinal(reader, "SourceImagesValue");
        if (sourceImagesOrdinal >= 0 && !reader.IsDBNull(sourceImagesOrdinal))
        {
            item.SourceImagesValue = reader.GetString(sourceImagesOrdinal);
        }

        var localImagesOrdinal = TryGetOrdinal(reader, "LocalImagesValue");
        if (localImagesOrdinal >= 0 && !reader.IsDBNull(localImagesOrdinal))
        {
            item.LocalImagesValue = reader.GetString(localImagesOrdinal);
        }

        var sourceImagesHashOrdinal = TryGetOrdinal(reader, "SourceImagesHash");
        if (sourceImagesHashOrdinal >= 0 && !reader.IsDBNull(sourceImagesHashOrdinal))
        {
            item.SourceImagesHash = reader.GetString(sourceImagesHashOrdinal);
        }

        var syncedImagesHashOrdinal = TryGetOrdinal(reader, "SyncedImagesHash");
        if (syncedImagesHashOrdinal >= 0 && !reader.IsDBNull(syncedImagesHashOrdinal))
        {
            item.SyncedImagesHash = reader.GetString(syncedImagesHashOrdinal);
        }

        // People category fields
        var sourcePeopleOrdinal = TryGetOrdinal(reader, "SourcePeopleValue");
        if (sourcePeopleOrdinal >= 0 && !reader.IsDBNull(sourcePeopleOrdinal))
        {
            item.SourcePeopleValue = reader.GetString(sourcePeopleOrdinal);
        }

        var localPeopleOrdinal = TryGetOrdinal(reader, "LocalPeopleValue");
        if (localPeopleOrdinal >= 0 && !reader.IsDBNull(localPeopleOrdinal))
        {
            item.LocalPeopleValue = reader.GetString(localPeopleOrdinal);
        }

        // Studios category fields
        var sourceStudiosOrdinal = TryGetOrdinal(reader, "SourceStudiosValue");
        if (sourceStudiosOrdinal >= 0 && !reader.IsDBNull(sourceStudiosOrdinal))
        {
            item.SourceStudiosValue = reader.GetString(sourceStudiosOrdinal);
        }

        var localStudiosOrdinal = TryGetOrdinal(reader, "LocalStudiosValue");
        if (localStudiosOrdinal >= 0 && !reader.IsDBNull(localStudiosOrdinal))
        {
            item.LocalStudiosValue = reader.GetString(localStudiosOrdinal);
        }

        // ETag for change detection
        var sourceETagOrdinal = TryGetOrdinal(reader, "SourceETag");
        if (sourceETagOrdinal >= 0 && !reader.IsDBNull(sourceETagOrdinal))
        {
            item.SourceETag = reader.GetString(sourceETagOrdinal);
        }

        // Folder item support fields
        var itemTypeOrdinal = TryGetOrdinal(reader, "ItemType");
        if (itemTypeOrdinal >= 0 && !reader.IsDBNull(itemTypeOrdinal))
        {
            item.ItemType = reader.GetString(itemTypeOrdinal);
        }

        var isFolderOrdinal = TryGetOrdinal(reader, "IsFolder");
        if (isFolderOrdinal >= 0 && !reader.IsDBNull(isFolderOrdinal))
        {
            item.IsFolder = reader.GetInt32(isFolderOrdinal) != 0;
        }

        var lastSyncTimeOrdinal = reader.GetOrdinal("LastSyncTime");
        if (!reader.IsDBNull(lastSyncTimeOrdinal))
        {
            item.LastSyncTime = ParseDateTimeSafe(reader.GetString(lastSyncTimeOrdinal));
        }

        var errorMessageOrdinal = reader.GetOrdinal("ErrorMessage");
        if (!reader.IsDBNull(errorMessageOrdinal))
        {
            item.ErrorMessage = reader.GetString(errorMessageOrdinal);
        }

        return item;
    }
}
