#pragma warning disable CA2100 // SQL is internal/parameterized.
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Database;
using JPKribs.Jellyfin.Base;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.MetadataSync;

/// <summary>
/// Manages the Metadata sync table, with one row for each source library and source item and a hash pair for each category.
/// The key has two columns, so every key route is overridden and <see cref="KeyColumn"/> goes unused.
/// </summary>
[PluginService(ServiceLifetime.Transient)]
public sealed class MetadataSyncTableManager
    : SyncTableManagerBase<MetadataSyncItem, (string SourceLibraryId, string SourceItemId)>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataSyncTableManager"/> class.
    /// </summary>
    /// <param name="databaseProvider">Gives the sync database.</param>
    /// <param name="logger">Writes log entries.</param>
    public MetadataSyncTableManager(ISyncDatabaseProvider databaseProvider, ILogger<MetadataSyncTableManager> logger)
        : base(databaseProvider, logger)
    {
    }

    /// <summary>
    /// Clears every category's synced hash when a row is queued, so the next run applies each category again.
    /// </summary>
    private static readonly string[] _queueClearsSyncedHashes =
    {
        "SyncedMetadataHash = NULL",
        "SyncedImagesHash = NULL",
        "SyncedPeopleHash = NULL",
        "SyncedStudiosHash = NULL",
        "SyncedSegmentsHash = NULL"
    };

    /// <inheritdoc />
    protected override string TableName => "MetadataSyncItems";

    /// <inheritdoc />
    protected override IReadOnlyList<string> OperatorQueueClauses => _queueClearsSyncedHashes;

    /// <inheritdoc />
    protected override string KeyColumn => "SourceItemId";

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

        item.Segments.Source = ReadNullableString(reader, "SourceSegmentsValue");
        item.Segments.Local = ReadNullableString(reader, "LocalSegmentsValue");
        item.Segments.SourceHash = ReadNullableString(reader, "SourceSegmentsHash");
        item.Segments.SyncedHash = ReadNullableString(reader, "SyncedSegmentsHash");

        item.LastSyncTime = ReadNullableDateTime(reader, "LastSyncTime");
        item.Reason = ReadNullableString(reader, "Reason");
        item.RetryCount = ReadNullableInt32(reader, "RetryCount") ?? 0;
        item.ServerKey = ReadNullableString(reader, "ServerKey");
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
        var upsert = new UpsertBuilder(TableName)
            .Key("SourceLibraryId", record.SourceLibraryId)
            .Key("SourceItemId", record.SourceItemId)
            .Set("LocalLibraryId", record.LocalLibraryId)
            .Set("LocalItemId", record.LocalItemId)
            .Set("ItemName", record.ItemName)
            .Set("SourcePath", record.SourcePath)
            .Set("LocalPath", record.LocalPath)
            .Set("ItemType", record.ItemType)
            .Set("IsFolder", record.IsFolder ? 1 : 0)
            .Set("SourceMetadataValue", record.Metadata.Source)
            .Set("LocalMetadataValue", record.Metadata.Local)
            .Set("SourceMetadataHash", record.Metadata.SourceHash)
            .SetUnlessNull("SyncedMetadataHash", record.Metadata.SyncedHash)
            .Set("SourceImagesValue", record.Images.Source)
            .Set("LocalImagesValue", record.Images.Local)
            .Set("SourceImagesHash", record.Images.SourceHash)
            .SetUnlessNull("SyncedImagesHash", record.Images.SyncedHash)
            .Set("SourcePeopleValue", record.People.Source)
            .Set("LocalPeopleValue", record.People.Local)
            .Set("SourcePeopleHash", record.People.SourceHash)
            .SetUnlessNull("SyncedPeopleHash", record.People.SyncedHash)
            .Set("SourceStudiosValue", record.Studios.Source)
            .Set("LocalStudiosValue", record.Studios.Local)
            .Set("SourceStudiosHash", record.Studios.SourceHash)
            .SetUnlessNull("SyncedStudiosHash", record.Studios.SyncedHash)
            .Set("SourceSegmentsValue", record.Segments.Source)
            .Set("LocalSegmentsValue", record.Segments.Local)
            .Set("SourceSegmentsHash", record.Segments.SourceHash)
            .SetUnlessNull("SyncedSegmentsHash", record.Segments.SyncedHash)
            .SyncColumns(record);
        ExecuteWrite(upsert.Execute);
    }

    /// <summary>
    /// Gets one page of metadata rows, filtered by text, status, and source library.
    /// Each row carries its source and local blobs, so its change flags compare the way the scan does.
    /// </summary>
    /// <param name="searchTerm">Text to find in the item name or source path, or null.</param>
    /// <param name="status">Only rows with this status, or null.</param>
    /// <param name="sourceLibraryId">Only rows from this source library, or null.</param>
    /// <param name="skip">How many matching rows to pass over.</param>
    /// <param name="take">The most rows to return.</param>
    /// <returns>The page of rows and the count of every matching row.</returns>
    public (IList<MetadataSyncItem> Items, int TotalCount) SearchMetadataSyncItemsPaginated(
        string? searchTerm = null,
        SyncStatus? status = null,
        string? sourceLibraryId = null,
        int skip = 0,
        int take = 50) => ExecuteRead<(IList<MetadataSyncItem> Items, int TotalCount)>(
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
            dataCmd.CommandText = PageQuery("MetadataSyncItems", whereClause, $"{StatusPriorityOrderBy}, ItemName ASC, Id ASC");
            BindFilters(dataCmd, searchTerm, status, sourceLibraryId);
            dataCmd.Parameters.AddWithValue("@take", take);
            dataCmd.Parameters.AddWithValue("@skip", skip);

            var items = new List<MetadataSyncItem>();
            using var reader = dataCmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(MapFromReader(reader));
            }

            return (items, totalCount);
        },
        fallback: (Array.Empty<MetadataSyncItem>(), 0));

    /// <inheritdoc />
    public override PagedResult<MetadataSyncItem> Paginate(PaginationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        var skip = (page - 1) * pageSize;
        var (items, total) = SearchMetadataSyncItemsPaginated(request.SearchTerm, request.StatusFilter, sourceLibraryId: null, skip, pageSize);
        return new PagedResult<MetadataSyncItem>((IReadOnlyList<MetadataSyncItem>)items, total, skip, pageSize);
    }

    /// <summary>
    /// Sets the status of many rows by id in one transaction, and reports the ids that matched no row.
    /// Moving to <see cref="SyncStatus.Queued"/> also clears every synced hash, so the next run applies each category again.
    /// </summary>
    /// <param name="ids">The row ids to update.</param>
    /// <param name="status">The status to move each row to.</param>
    /// <param name="reason">The reason to store, for a status that carries one.</param>
    /// <returns>How many rows were updated, and the ids that were not found.</returns>
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
                clauses.AddRange(OperatorQueueClauses);
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

    /// <summary>
    /// Binds the parameters of each filter that is set.
    /// </summary>
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
