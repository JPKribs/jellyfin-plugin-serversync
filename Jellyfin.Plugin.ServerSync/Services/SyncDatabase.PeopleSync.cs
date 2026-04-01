using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

#pragma warning disable CA2100 // SQL commands use only internal constants and safe parameterized queries

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// SyncDatabase — People Sync methods.
/// </summary>
public partial class SyncDatabase
{
    // ============================================
    // People Sync Methods
    // ============================================

    /// <summary>
    /// Gets a people sync item by person name.
    /// </summary>
    public PeopleSyncItem? GetPeopleSyncItemByName(string personName)
    {
        return ExecuteRead(() =>
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM PeopleSyncItems WHERE PersonName = @personName";
            command.Parameters.AddWithValue("@personName", personName);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadPeopleSyncItem(reader) : null;
        }, fallbackValue: null);
    }

    /// <summary>
    /// Gets all people sync items.
    /// </summary>
    public List<PeopleSyncItem> GetAllPeopleSyncItems()
    {
        return ExecuteRead(() =>
        {
            var items = new List<PeopleSyncItem>();
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM PeopleSyncItems";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadPeopleSyncItem(reader));
            }

            return items;
        }, fallbackValue: new List<PeopleSyncItem>());
    }

    /// <summary>
    /// Gets all people sync items with a specific status.
    /// </summary>
    public List<PeopleSyncItem> GetPeopleSyncItemsByStatus(BaseSyncStatus status)
    {
        return ExecuteRead(() =>
        {
            var items = new List<PeopleSyncItem>();
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM PeopleSyncItems WHERE Status = @status";
            command.Parameters.AddWithValue("@status", (int)status);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadPeopleSyncItem(reader));
            }

            return items;
        }, fallbackValue: new List<PeopleSyncItem>());
    }

    /// <summary>
    /// Gets people sync status counts.
    /// </summary>
    public Dictionary<BaseSyncStatus, int> GetPeopleSyncStatusCounts()
    {
        return ExecuteRead(() =>
        {
            var counts = new Dictionary<BaseSyncStatus, int>();
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT Status, COUNT(*) as Count FROM PeopleSyncItems GROUP BY Status";

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
    /// Upserts a people sync item (matched by PersonName).
    /// Preserves Ignored status.
    /// </summary>
    public void UpsertPeopleSyncItem(PeopleSyncItem item)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            using var command = _connection!.CreateCommand();
            command.CommandText = @"
                INSERT INTO PeopleSyncItems (
                    PersonName, SourcePersonId, LocalPersonId,
                    SourceOverview, LocalOverview,
                    SourceProviderIds, LocalProviderIds,
                    SourceImagesValue, LocalImagesValue,
                    SourceImagesHash, SyncedImagesHash,
                    Status, StatusDate, LastSyncTime, ErrorMessage
                ) VALUES (
                    @personName, @sourcePersonId, @localPersonId,
                    @sourceOverview, @localOverview,
                    @sourceProviderIds, @localProviderIds,
                    @sourceImagesValue, @localImagesValue,
                    @sourceImagesHash, @syncedImagesHash,
                    @status, @statusDate, @lastSyncTime, @errorMessage
                )
                ON CONFLICT(PersonName) DO UPDATE SET
                    SourcePersonId = @sourcePersonId,
                    LocalPersonId = @localPersonId,
                    SourceOverview = @sourceOverview,
                    LocalOverview = @localOverview,
                    SourceProviderIds = @sourceProviderIds,
                    LocalProviderIds = @localProviderIds,
                    SourceImagesValue = @sourceImagesValue,
                    LocalImagesValue = @localImagesValue,
                    SourceImagesHash = @sourceImagesHash,
                    SyncedImagesHash = CASE
                        WHEN @syncedImagesHash IS NOT NULL THEN @syncedImagesHash
                        ELSE PeopleSyncItems.SyncedImagesHash
                    END,
                    Status = CASE
                        WHEN PeopleSyncItems.Status = @ignoredStatus THEN @ignoredStatus
                        ELSE @status
                    END,
                    StatusDate = CASE
                        WHEN PeopleSyncItems.Status = @ignoredStatus THEN PeopleSyncItems.StatusDate
                        ELSE @statusDate
                    END,
                    LastSyncTime = CASE
                        WHEN PeopleSyncItems.Status = @ignoredStatus THEN PeopleSyncItems.LastSyncTime
                        ELSE @lastSyncTime
                    END,
                    ErrorMessage = CASE
                        WHEN PeopleSyncItems.Status = @ignoredStatus THEN PeopleSyncItems.ErrorMessage
                        ELSE @errorMessage
                    END
            ";

            command.Parameters.AddWithValue("@personName", item.PersonName);
            command.Parameters.AddWithValue("@sourcePersonId", item.SourcePersonId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localPersonId", item.LocalPersonId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourceOverview", item.SourceOverview ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localOverview", item.LocalOverview ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourceProviderIds", item.SourceProviderIds ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localProviderIds", item.LocalProviderIds ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourceImagesValue", item.SourceImagesValue ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localImagesValue", item.LocalImagesValue ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourceImagesHash", item.SourceImagesHash ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@syncedImagesHash", item.SyncedImagesHash ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@status", (int)item.Status);
            command.Parameters.AddWithValue("@statusDate", item.StatusDate.ToString("o"));
            command.Parameters.AddWithValue("@lastSyncTime", item.LastSyncTime?.ToString("o") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@errorMessage", item.ErrorMessage ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@ignoredStatus", (int)BaseSyncStatus.Ignored);

            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Updates the status of a people sync item by database ID.
    /// </summary>
    public void UpdatePeopleSyncItemStatusById(long id, BaseSyncStatus status, string? errorMessage = null)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            using var command = _connection!.CreateCommand();

            if (status == BaseSyncStatus.Synced)
            {
                command.CommandText = @"
                    UPDATE PeopleSyncItems
                    SET Status = @status,
                        StatusDate = @statusDate,
                        LastSyncTime = @statusDate,
                        ErrorMessage = NULL
                    WHERE Id = @id";
            }
            else
            {
                command.CommandText = @"
                    UPDATE PeopleSyncItems
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
    /// Deletes a people sync item by person name.
    /// </summary>
    public int DeletePeopleSyncItemByName(string personName)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            using var command = _connection!.CreateCommand();
            command.CommandText = "DELETE FROM PeopleSyncItems WHERE PersonName = @personName";
            command.Parameters.AddWithValue("@personName", personName);

            return command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Resets the people sync database by clearing all items.
    /// </summary>
    public void ResetPeopleSyncDatabase()
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            _logger.LogWarning("Resetting people sync database - all people sync tracking data will be lost");

            try
            {
                EnsureConnection();
                using var command = _connection!.CreateCommand();
                command.CommandText = "DELETE FROM PeopleSyncItems WHERE 1=1";
                var deleted = command.ExecuteNonQuery();
                _logger.LogInformation("People sync database has been reset, {Count} items removed", deleted);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                _logger.LogInformation("People sync table does not exist yet, nothing to reset");
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 8)
            {
                _logger.LogWarning(ex, "Database is readonly, falling back to full database recreation");
                RecreateDatabase();
            }
        }
    }

    /// <summary>
    /// Gets a people sync item by database ID.
    /// </summary>
    public PeopleSyncItem? GetPeopleSyncItemById(long id)
    {
        return ExecuteRead(() =>
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM PeopleSyncItems WHERE Id = @id";
            command.Parameters.AddWithValue("@id", id);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadPeopleSyncItem(reader) : null;
        }, fallbackValue: null);
    }

    /// <summary>
    /// Searches people sync items with pagination, optional search and status filter.
    /// </summary>
    public (List<PeopleSyncItem> Items, int TotalCount) SearchPeopleSyncItemsPaginated(
        string? searchTerm = null,
        BaseSyncStatus? status = null,
        int skip = 0,
        int take = 50)
    {
        return ExecuteRead(() =>
        {
            var conditions = new List<string>();

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                conditions.Add("PersonName LIKE @searchTerm");
            }

            if (status.HasValue)
            {
                conditions.Add("Status = @status");
            }

            var whereClause = conditions.Count > 0
                ? $"WHERE {string.Join(" AND ", conditions)}"
                : string.Empty;

            // Get total count
            int totalCount;
            using (var countCommand = _connection!.CreateCommand())
            {
                countCommand.CommandText = $"SELECT COUNT(*) FROM PeopleSyncItems {whereClause}";

                if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    countCommand.Parameters.AddWithValue("@searchTerm", $"%{searchTerm}%");
                }

                if (status.HasValue)
                {
                    countCommand.Parameters.AddWithValue("@status", (int)status.Value);
                }

                totalCount = Convert.ToInt32(countCommand.ExecuteScalar());
            }

            // Get paginated data
            var items = new List<PeopleSyncItem>();
            using (var dataCommand = _connection!.CreateCommand())
            {
                dataCommand.CommandText = $@"
                    SELECT * FROM PeopleSyncItems
                    {whereClause}
                    ORDER BY
                        CASE Status
                            WHEN 3 THEN 0  -- Errored first
                            WHEN 1 THEN 1  -- Queued second
                            WHEN 0 THEN 2  -- Pending third
                            WHEN 4 THEN 3  -- Ignored fourth
                            WHEN 2 THEN 4  -- Synced last
                            ELSE 5
                        END,
                        PersonName ASC
                    LIMIT @take OFFSET @skip";

                if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    dataCommand.Parameters.AddWithValue("@searchTerm", $"%{searchTerm}%");
                }

                if (status.HasValue)
                {
                    dataCommand.Parameters.AddWithValue("@status", (int)status.Value);
                }

                dataCommand.Parameters.AddWithValue("@take", take);
                dataCommand.Parameters.AddWithValue("@skip", skip);

                using var reader = dataCommand.ExecuteReader();
                while (reader.Read())
                {
                    items.Add(ReadPeopleSyncItem(reader));
                }
            }

            return (items, totalCount);
        }, fallbackValue: (new List<PeopleSyncItem>(), 0));
    }

    /// <summary>
    /// Batch updates the status of multiple people sync items by their database IDs.
    /// </summary>
    public int BatchUpdatePeopleSyncItemStatusByIds(IEnumerable<long> ids, BaseSyncStatus status)
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
                        UPDATE PeopleSyncItems
                        SET Status = @status,
                            StatusDate = @statusDate,
                            SyncedImagesHash = NULL
                        WHERE Id = @id";
                }
                else
                {
                    command.CommandText = @"
                        UPDATE PeopleSyncItems
                        SET Status = @status,
                            StatusDate = @statusDate
                        WHERE Id = @id";
                }

                var idParam = new SqliteParameter("@id", 0L);
                var statusDateParam = new SqliteParameter("@statusDate", DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
                command.Parameters.Add(idParam);
                command.Parameters.Add(new SqliteParameter("@status", (int)status));
                command.Parameters.Add(statusDateParam);

                foreach (var id in ids)
                {
                    idParam.Value = id;
                    count += command.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception)
            {
                transaction.Rollback();
                throw;
            }

            return count;
        }
    }

    /// <summary>
    /// Reads a PeopleSyncItem from the database reader.
    /// </summary>
    private static PeopleSyncItem ReadPeopleSyncItem(SqliteDataReader reader)
    {
        var item = new PeopleSyncItem
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            PersonName = reader.GetString(reader.GetOrdinal("PersonName")),
            Status = (BaseSyncStatus)reader.GetInt32(reader.GetOrdinal("Status")),
            StatusDate = ParseDateTimeSafe(reader.GetString(reader.GetOrdinal("StatusDate")))
        };

        var sourcePersonIdOrdinal = reader.GetOrdinal("SourcePersonId");
        if (!reader.IsDBNull(sourcePersonIdOrdinal))
        {
            item.SourcePersonId = reader.GetString(sourcePersonIdOrdinal);
        }

        var localPersonIdOrdinal = reader.GetOrdinal("LocalPersonId");
        if (!reader.IsDBNull(localPersonIdOrdinal))
        {
            item.LocalPersonId = reader.GetString(localPersonIdOrdinal);
        }

        var sourceOverviewOrdinal = reader.GetOrdinal("SourceOverview");
        if (!reader.IsDBNull(sourceOverviewOrdinal))
        {
            item.SourceOverview = reader.GetString(sourceOverviewOrdinal);
        }

        var localOverviewOrdinal = reader.GetOrdinal("LocalOverview");
        if (!reader.IsDBNull(localOverviewOrdinal))
        {
            item.LocalOverview = reader.GetString(localOverviewOrdinal);
        }

        var sourceProviderIdsOrdinal = reader.GetOrdinal("SourceProviderIds");
        if (!reader.IsDBNull(sourceProviderIdsOrdinal))
        {
            item.SourceProviderIds = reader.GetString(sourceProviderIdsOrdinal);
        }

        var localProviderIdsOrdinal = reader.GetOrdinal("LocalProviderIds");
        if (!reader.IsDBNull(localProviderIdsOrdinal))
        {
            item.LocalProviderIds = reader.GetString(localProviderIdsOrdinal);
        }

        var sourceImagesOrdinal = reader.GetOrdinal("SourceImagesValue");
        if (!reader.IsDBNull(sourceImagesOrdinal))
        {
            item.SourceImagesValue = reader.GetString(sourceImagesOrdinal);
        }

        var localImagesOrdinal = reader.GetOrdinal("LocalImagesValue");
        if (!reader.IsDBNull(localImagesOrdinal))
        {
            item.LocalImagesValue = reader.GetString(localImagesOrdinal);
        }

        var sourceImagesHashOrdinal = reader.GetOrdinal("SourceImagesHash");
        if (!reader.IsDBNull(sourceImagesHashOrdinal))
        {
            item.SourceImagesHash = reader.GetString(sourceImagesHashOrdinal);
        }

        var syncedImagesHashOrdinal = reader.GetOrdinal("SyncedImagesHash");
        if (!reader.IsDBNull(syncedImagesHashOrdinal))
        {
            item.SyncedImagesHash = reader.GetString(syncedImagesHashOrdinal);
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
