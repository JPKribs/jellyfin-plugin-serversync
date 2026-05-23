#pragma warning disable CA2100 // SQL is internal/parameterized.
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Per-table manager for <see cref="MetadataSyncItem"/>. Natural key is the
/// composite (SourceLibraryId, SourceItemId) — one row per source-side item
/// with four parallel <see cref="SyncableValue{T}"/> categories
/// (Metadata, Images, People, Studios), each with its own
/// Source/SyncedHash columns to drive the per-category short-circuit on
/// Refresh.
/// </summary>
[PluginService(ServiceLifetime.Transient)]
public sealed class MetadataSyncTableManager
    : SyncTableManagerBase<MetadataSyncItem, (string SourceLibraryId, string SourceItemId)>
{
    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public MetadataSyncTableManager(ISyncDatabaseProvider databaseProvider, ILogger<MetadataSyncTableManager> logger)
        : base(GetDatabase(databaseProvider), logger)
    {
    }

    private static SyncDatabase GetDatabase(ISyncDatabaseProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.Database;
    }

    /// <inheritdoc />
    protected override string TableName => "MetadataSyncItems";

    // Sentinel — UpdateStatusByKey is overridden directly to handle the
    // composite key.
    /// <inheritdoc />
    protected override string KeyColumn => "SourceItemId";

    // Order: Errored, Queued, Ignored, Synced. No Pending in this table.
    /// <inheritdoc />
    protected override string StatusPriorityOrderBy => @"
        CASE Status
            WHEN 3 THEN 0
            WHEN 1 THEN 1
            WHEN 4 THEN 2
            WHEN 2 THEN 3
            ELSE 4
        END";

    /// <inheritdoc />
    protected override MetadataSyncItem MapFromReader(IDataRecord reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var item = new MetadataSyncItem
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            SourceLibraryId = reader.GetString(reader.GetOrdinal("SourceLibraryId")),
            LocalLibraryId = reader.GetString(reader.GetOrdinal("LocalLibraryId")),
            SourceItemId = reader.GetString(reader.GetOrdinal("SourceItemId")),
            Status = (SyncStatus)reader.GetInt32(reader.GetOrdinal("Status")),
            StatusDate = ReadDateTime(reader, "StatusDate")
        };

        item.LocalItemId = ReadNullableString(reader, "LocalItemId");
        item.ItemName = ReadNullableString(reader, "ItemName");
        item.SourcePath = ReadNullableString(reader, "SourcePath");
        item.LocalPath = ReadNullableString(reader, "LocalPath");
        item.ItemType = ReadNullableString(reader, "ItemType");

        var isFolderOrd = reader.GetOrdinal("IsFolder");
        if (!reader.IsDBNull(isFolderOrd))
        {
            item.IsFolder = reader.GetInt32(isFolderOrd) != 0;
        }

        // Bridge property setters recompute the source hash; bypass them by
        // setting the underlying SyncableValue fields so the stored hash is
        // preserved as-written.
        item.Metadata.Source = ReadNullableString(reader, "SourceMetadataValue");
        item.Metadata.Local = ReadNullableString(reader, "LocalMetadataValue");
        item.Metadata.SourceHash = ReadNullableString(reader, "SourceMetadataHash");
        item.Metadata.SyncedHash = ReadNullableString(reader, "SyncedMetadataHash");

        item.Images.Source = ReadNullableString(reader, "SourceImagesValue");
        item.Images.Local = ReadNullableString(reader, "LocalImagesValue");
        item.Images.SourceHash = ReadNullableString(reader, "SourceImagesHash");
        item.Images.SyncedHash = ReadNullableString(reader, "SyncedImagesHash");

        item.People.Source = ReadNullableString(reader, "SourcePeopleValue");
        item.People.Local = ReadNullableString(reader, "LocalPeopleValue");
        item.People.SourceHash = ReadNullableString(reader, "SourcePeopleHash");
        item.People.SyncedHash = ReadNullableString(reader, "SyncedPeopleHash");

        item.Studios.Source = ReadNullableString(reader, "SourceStudiosValue");
        item.Studios.Local = ReadNullableString(reader, "LocalStudiosValue");
        item.Studios.SourceHash = ReadNullableString(reader, "SourceStudiosHash");
        item.Studios.SyncedHash = ReadNullableString(reader, "SyncedStudiosHash");

        item.LastSyncTime = ReadNullableDateTime(reader, "LastSyncTime");
        item.Reason = ReadNullableString(reader, "Reason");
        return item;
    }

    /// <inheritdoc />
    public override MetadataSyncItem? GetByKey((string SourceLibraryId, string SourceItemId) key) => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM MetadataSyncItems WHERE SourceLibraryId = @lib AND SourceItemId = @item LIMIT 1";
            cmd.Parameters.AddWithValue("@lib", key.SourceLibraryId);
            cmd.Parameters.AddWithValue("@item", key.SourceItemId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? MapFromReader(reader) : null;
        },
        fallback: null);

    /// <inheritdoc />
    public override void DeleteByKey((string SourceLibraryId, string SourceItemId) key) => ExecuteWrite(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM MetadataSyncItems WHERE SourceLibraryId = @lib AND SourceItemId = @item";
        cmd.Parameters.AddWithValue("@lib", key.SourceLibraryId);
        cmd.Parameters.AddWithValue("@item", key.SourceItemId);
        cmd.ExecuteNonQuery();
    });

    /// <inheritdoc />
    public override void UpdateStatusByKey(
        (string SourceLibraryId, string SourceItemId) key,
        SyncStatus status,
        string? reason = null) => ExecuteWrite(conn =>
    {
        using var cmd = conn.CreateCommand();
        var clauses = BuildStatusTransitionClauses(status);
        cmd.CommandText = $@"
            UPDATE MetadataSyncItems SET {string.Join(", ", clauses)}
            WHERE SourceLibraryId = @lib AND SourceItemId = @item";
        AddStatusTransitionParameters(cmd, status, reason);
        cmd.Parameters.AddWithValue("@lib", key.SourceLibraryId);
        cmd.Parameters.AddWithValue("@item", key.SourceItemId);
        cmd.ExecuteNonQuery();
    });

    /// <inheritdoc />
    public override void Upsert(MetadataSyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO MetadataSyncItems (
                    SourceLibraryId, LocalLibraryId, SourceItemId, LocalItemId,
                    ItemName, SourcePath, LocalPath, ItemType, IsFolder,
                    SourceMetadataValue, LocalMetadataValue, SourceMetadataHash, SyncedMetadataHash,
                    SourceImagesValue, LocalImagesValue, SourceImagesHash, SyncedImagesHash,
                    SourcePeopleValue, LocalPeopleValue, SourcePeopleHash, SyncedPeopleHash,
                    SourceStudiosValue, LocalStudiosValue, SourceStudiosHash, SyncedStudiosHash,
                    Status, StatusDate, LastSyncTime, Reason
                ) VALUES (
                    @sourceLibraryId, @localLibraryId, @sourceItemId, @localItemId,
                    @itemName, @sourcePath, @localPath, @itemType, @isFolder,
                    @srcMeta, @locMeta, @srcMetaHash, @syncedMetaHash,
                    @srcImg, @locImg, @srcImgHash, @syncedImgHash,
                    @srcPeople, @locPeople, @srcPeopleHash, @syncedPeopleHash,
                    @srcStudios, @locStudios, @srcStudiosHash, @syncedStudiosHash,
                    @status, @statusDate, @lastSync, @reason
                )
                ON CONFLICT(SourceLibraryId, SourceItemId) DO UPDATE SET
                    LocalLibraryId = @localLibraryId,
                    LocalItemId = @localItemId,
                    ItemName = @itemName,
                    SourcePath = @sourcePath,
                    LocalPath = @localPath,
                    ItemType = @itemType,
                    IsFolder = @isFolder,
                    SourceMetadataValue = @srcMeta,
                    LocalMetadataValue = @locMeta,
                    SourceMetadataHash = @srcMetaHash,
                    SyncedMetadataHash = CASE WHEN @syncedMetaHash IS NOT NULL THEN @syncedMetaHash ELSE MetadataSyncItems.SyncedMetadataHash END,
                    SourceImagesValue = @srcImg,
                    LocalImagesValue = @locImg,
                    SourceImagesHash = @srcImgHash,
                    SyncedImagesHash = CASE WHEN @syncedImgHash IS NOT NULL THEN @syncedImgHash ELSE MetadataSyncItems.SyncedImagesHash END,
                    SourcePeopleValue = @srcPeople,
                    LocalPeopleValue = @locPeople,
                    SourcePeopleHash = @srcPeopleHash,
                    SyncedPeopleHash = CASE WHEN @syncedPeopleHash IS NOT NULL THEN @syncedPeopleHash ELSE MetadataSyncItems.SyncedPeopleHash END,
                    SourceStudiosValue = @srcStudios,
                    LocalStudiosValue = @locStudios,
                    SourceStudiosHash = @srcStudiosHash,
                    SyncedStudiosHash = CASE WHEN @syncedStudiosHash IS NOT NULL THEN @syncedStudiosHash ELSE MetadataSyncItems.SyncedStudiosHash END,
                    Status = CASE WHEN MetadataSyncItems.Status = @ignoredStatus THEN @ignoredStatus ELSE @status END,
                    StatusDate = CASE WHEN MetadataSyncItems.Status = @ignoredStatus THEN MetadataSyncItems.StatusDate ELSE @statusDate END,
                    LastSyncTime = CASE WHEN MetadataSyncItems.Status = @ignoredStatus THEN MetadataSyncItems.LastSyncTime ELSE @lastSync END,
                    Reason = CASE WHEN MetadataSyncItems.Status = @ignoredStatus THEN MetadataSyncItems.Reason ELSE @reason END";

            cmd.Parameters.AddWithValue("@sourceLibraryId", record.SourceLibraryId);
            cmd.Parameters.AddWithValue("@localLibraryId", record.LocalLibraryId);
            cmd.Parameters.AddWithValue("@sourceItemId", record.SourceItemId);
            AddNullable(cmd, "@localItemId", record.LocalItemId);
            AddNullable(cmd, "@itemName", record.ItemName);
            AddNullable(cmd, "@sourcePath", record.SourcePath);
            AddNullable(cmd, "@localPath", record.LocalPath);
            AddNullable(cmd, "@itemType", record.ItemType);
            cmd.Parameters.AddWithValue("@isFolder", record.IsFolder ? 1 : 0);

            AddNullable(cmd, "@srcMeta", record.Metadata.Source);
            AddNullable(cmd, "@locMeta", record.Metadata.Local);
            AddNullable(cmd, "@srcMetaHash", record.Metadata.SourceHash);
            AddNullable(cmd, "@syncedMetaHash", record.Metadata.SyncedHash);

            AddNullable(cmd, "@srcImg", record.Images.Source);
            AddNullable(cmd, "@locImg", record.Images.Local);
            AddNullable(cmd, "@srcImgHash", record.Images.SourceHash);
            AddNullable(cmd, "@syncedImgHash", record.Images.SyncedHash);

            AddNullable(cmd, "@srcPeople", record.People.Source);
            AddNullable(cmd, "@locPeople", record.People.Local);
            AddNullable(cmd, "@srcPeopleHash", record.People.SourceHash);
            AddNullable(cmd, "@syncedPeopleHash", record.People.SyncedHash);

            AddNullable(cmd, "@srcStudios", record.Studios.Source);
            AddNullable(cmd, "@locStudios", record.Studios.Local);
            AddNullable(cmd, "@srcStudiosHash", record.Studios.SourceHash);
            AddNullable(cmd, "@syncedStudiosHash", record.Studios.SyncedHash);

            cmd.Parameters.AddWithValue("@status", (int)record.Status);
            AddTimestamp(cmd, "@statusDate", record.StatusDate);
            AddNullableTimestamp(cmd, "@lastSync", record.LastSyncTime);
            AddNullable(cmd, "@reason", record.Reason);
            cmd.Parameters.AddWithValue("@ignoredStatus", (int)SyncStatus.Ignored);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Returns all metadata rows for a source library.
    /// </summary>
    public IList<MetadataSyncItem> GetByLibrary(string sourceLibraryId) => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM MetadataSyncItems WHERE SourceLibraryId = @lib";
            cmd.Parameters.AddWithValue("@lib", sourceLibraryId);
            return ReadAll(cmd);
        },
        fallback: (IList<MetadataSyncItem>)Array.Empty<MetadataSyncItem>());

    /// <summary>
    /// Searches metadata items with optional filters. Returns a lightweight
    /// projection that omits the four <c>Source*Value</c> / <c>Local*Value</c>
    /// JSON blob columns (several KB each); hashes are loaded so callers can
    /// compute per-category change flags via hash mismatch. Use
    /// <see cref="GetByKey"/> when blobs are needed.
    /// </summary>
    public (IList<MetadataSyncItem> Items, int TotalCount) SearchMetadataSyncItemsPaginated(
        string? searchTerm = null,
        SyncStatus? status = null,
        string? sourceLibraryId = null,
        int skip = 0,
        int take = 50) => ExecuteRead(
        conn =>
        {
            var conditions = new List<string>();
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                conditions.Add("(ItemName LIKE @search OR SourcePath LIKE @search)");
            }

            if (status.HasValue)
            {
                conditions.Add("Status = @status");
            }

            if (!string.IsNullOrWhiteSpace(sourceLibraryId))
            {
                conditions.Add("SourceLibraryId = @sourceLibraryId");
            }

            var whereClause = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : string.Empty;

            int totalCount;
            using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = $"SELECT COUNT(*) FROM MetadataSyncItems {whereClause}";
                BindFilters(countCmd, searchTerm, status, sourceLibraryId);
                totalCount = Convert.ToInt32(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            using var dataCmd = conn.CreateCommand();
            dataCmd.CommandText = $@"
                SELECT
                    Id, SourceLibraryId, LocalLibraryId, SourceItemId, LocalItemId,
                    ItemName, SourcePath, LocalPath, ItemType, IsFolder,
                    SourceMetadataHash, SyncedMetadataHash,
                    SourceImagesHash, SyncedImagesHash,
                    SourcePeopleHash, SyncedPeopleHash,
                    SourceStudiosHash, SyncedStudiosHash,
                    Status, StatusDate, LastSyncTime, Reason
                FROM MetadataSyncItems
                {whereClause}
                ORDER BY {StatusPriorityOrderBy}, ItemName ASC, Id ASC
                LIMIT @take OFFSET @skip";
            BindFilters(dataCmd, searchTerm, status, sourceLibraryId);
            dataCmd.Parameters.AddWithValue("@take", take);
            dataCmd.Parameters.AddWithValue("@skip", skip);

            var items = new List<MetadataSyncItem>();
            using var reader = dataCmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(MapFromReaderListView(reader));
            }

            return ((IList<MetadataSyncItem>)items, totalCount);
        },
        fallback: ((IList<MetadataSyncItem>)Array.Empty<MetadataSyncItem>(), 0));

    /// <summary>
    /// Reads the lightweight column set used by the list view. Source/Local
    /// blob fields are intentionally left null; only hashes are populated so
    /// the caller can detect per-category changes via hash mismatch without
    /// shipping megabytes of JSON to the UI.
    /// </summary>
    private static MetadataSyncItem MapFromReaderListView(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var item = new MetadataSyncItem
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            SourceLibraryId = reader.GetString(reader.GetOrdinal("SourceLibraryId")),
            LocalLibraryId = reader.GetString(reader.GetOrdinal("LocalLibraryId")),
            SourceItemId = reader.GetString(reader.GetOrdinal("SourceItemId")),
            Status = (SyncStatus)reader.GetInt32(reader.GetOrdinal("Status")),
            StatusDate = ReadDateTime(reader, "StatusDate")
        };

        item.LocalItemId = ReadNullableString(reader, "LocalItemId");
        item.ItemName = ReadNullableString(reader, "ItemName");
        item.SourcePath = ReadNullableString(reader, "SourcePath");
        item.LocalPath = ReadNullableString(reader, "LocalPath");
        item.ItemType = ReadNullableString(reader, "ItemType");

        var isFolderOrd = reader.GetOrdinal("IsFolder");
        if (!reader.IsDBNull(isFolderOrd))
        {
            item.IsFolder = reader.GetInt32(isFolderOrd) != 0;
        }

        item.Metadata.SourceHash = ReadNullableString(reader, "SourceMetadataHash");
        item.Metadata.SyncedHash = ReadNullableString(reader, "SyncedMetadataHash");
        item.Images.SourceHash = ReadNullableString(reader, "SourceImagesHash");
        item.Images.SyncedHash = ReadNullableString(reader, "SyncedImagesHash");
        item.People.SourceHash = ReadNullableString(reader, "SourcePeopleHash");
        item.People.SyncedHash = ReadNullableString(reader, "SyncedPeopleHash");
        item.Studios.SourceHash = ReadNullableString(reader, "SourceStudiosHash");
        item.Studios.SyncedHash = ReadNullableString(reader, "SyncedStudiosHash");

        item.LastSyncTime = ReadNullableDateTime(reader, "LastSyncTime");
        item.Reason = ReadNullableString(reader, "Reason");
        return item;
    }

    /// <inheritdoc />
    public override PagedResult<MetadataSyncItem> Paginate(PaginationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        var skip = (page - 1) * pageSize;
        var (items, total) = SearchMetadataSyncItemsPaginated(request.SearchTerm, request.StatusFilter, sourceLibraryId: null, skip, pageSize);
        return new PagedResult<MetadataSyncItem>
        {
            Items = (IReadOnlyList<MetadataSyncItem>)items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// Returns all non-empty source-people JSON values across the table.
    /// Lightweight column-only query used for cross-item person-name
    /// aggregation (avoids loading full records). Skips empty arrays.
    /// </summary>
    public IList<string> GetAllSourcePeopleValues() => ExecuteRead(
        conn =>
        {
            var values = new List<string>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT SourcePeopleValue FROM MetadataSyncItems WHERE SourcePeopleValue IS NOT NULL AND SourcePeopleValue != '[]'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var v = reader.GetString(0);
                if (!string.IsNullOrEmpty(v))
                {
                    values.Add(v);
                }
            }

            return (IList<string>)values;
        },
        fallback: (IList<string>)Array.Empty<string>());

    /// <summary>
    /// Updates the status of many items by primary-key Id in one transaction.
    /// When transitioning to <see cref="SyncStatus.Queued"/>, also clears all
    /// four <c>Synced*Hash</c> columns so the next Sync run is forced to
    /// re-apply each category — preserves the historical "re-queue forces
    /// re-sync" semantic that the partial's bespoke method had for images.
    /// </summary>
    public int BatchUpdateStatusByIds(IEnumerable<long> ids, SyncStatus status, string? reason = null)
    {
        var result = BatchUpdateStatusByIdsWithDetails(ids, status, reason);
        return result.Updated;
    }

    /// <summary>
    /// Same as <see cref="BatchUpdateStatusByIds"/> but reports the
    /// per-call breakdown: rows updated, plus the input IDs that didn't
    /// match (typically: row was deleted between the user's click and the
    /// request landing). Bulk endpoints call this so the UI can surface
    /// "5 of 10 items updated; 5 not found" instead of silently swallowing
    /// the partial failure.
    /// </summary>
    public (int Updated, IReadOnlyList<long> NotFoundIds) BatchUpdateStatusByIdsWithDetails(
        IEnumerable<long> ids,
        SyncStatus status,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var count = 0;
        var notFound = new List<long>();
        ExecuteWrite(conn =>
        {
            using var transaction = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            var clauses = BuildStatusTransitionClauses(status);
            if (status == SyncStatus.Queued)
            {
                clauses.Add("SyncedMetadataHash = NULL");
                clauses.Add("SyncedImagesHash = NULL");
                clauses.Add("SyncedPeopleHash = NULL");
                clauses.Add("SyncedStudiosHash = NULL");
            }

            cmd.CommandText = $"UPDATE MetadataSyncItems SET {string.Join(", ", clauses)} WHERE Id = @Id";
            AddStatusTransitionParameters(cmd, status, reason);
            var idParam = cmd.Parameters.Add("@Id", SqliteType.Integer);
            foreach (var id in ids)
            {
                idParam.Value = id;
                var rows = cmd.ExecuteNonQuery();
                if (rows > 0)
                {
                    count += rows;
                }
                else
                {
                    notFound.Add(id);
                }
            }

            transaction.Commit();
        });
        return (count, notFound);
    }


    private static void BindFilters(SqliteCommand cmd, string? searchTerm, SyncStatus? status, string? sourceLibraryId)
    {
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            cmd.Parameters.AddWithValue("@search", $"%{searchTerm}%");
        }

        if (status.HasValue)
        {
            cmd.Parameters.AddWithValue("@status", (int)status.Value);
        }

        if (!string.IsNullOrWhiteSpace(sourceLibraryId))
        {
            cmd.Parameters.AddWithValue("@sourceLibraryId", sourceLibraryId);
        }
    }
}
