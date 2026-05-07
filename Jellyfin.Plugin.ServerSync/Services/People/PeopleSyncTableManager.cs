#pragma warning disable CA2100 // SQL is internal/parameterized.
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Per-table manager for <see cref="PeopleSyncItem"/>. Replaces the old
/// <c>SyncDatabase.PeopleSync.cs</c> partial-class methods.
/// </summary>
public sealed class PeopleSyncTableManager : SyncTableManagerBase<PeopleSyncItem, string>
{
    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public PeopleSyncTableManager(ISyncDatabaseProvider databaseProvider, ILogger<PeopleSyncTableManager> logger)
        : base(GetDatabase(databaseProvider), logger)
    {
    }

    private static SyncDatabase GetDatabase(ISyncDatabaseProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.Database;
    }

    /// <inheritdoc />
    protected override string TableName => "PeopleSyncItems";

    /// <inheritdoc />
    protected override string KeyColumn => "PersonName";

    /// <inheritdoc />
    protected override PeopleSyncItem MapFromReader(IDataRecord reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var item = new PeopleSyncItem
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            PersonName = reader.GetString(reader.GetOrdinal("PersonName")),
            Status = (SyncStatus)reader.GetInt32(reader.GetOrdinal("Status")),
            StatusDate = ReadDateTime(reader, "StatusDate")
        };

        item.SourcePersonId = ReadNullableString(reader, "SourcePersonId");
        item.LocalPersonId = ReadNullableString(reader, "LocalPersonId");

        // Bridge property setters are skipped to avoid recomputing hashes from
        // stored values; we set the underlying SyncableValue fields directly.
        item.Metadata.Source = ReadNullableString(reader, "SourceMetadataValue");
        item.Metadata.Local = ReadNullableString(reader, "LocalMetadataValue");
        item.Metadata.SourceHash = ReadNullableString(reader, "SourceMetadataHash");
        item.Metadata.SyncedHash = ReadNullableString(reader, "SyncedMetadataHash");

        item.Images.Source = ReadNullableString(reader, "SourceImagesValue");
        item.Images.Local = ReadNullableString(reader, "LocalImagesValue");
        item.Images.SourceHash = ReadNullableString(reader, "SourceImagesHash");
        item.Images.SyncedHash = ReadNullableString(reader, "SyncedImagesHash");

        item.LastSyncTime = ReadNullableDateTime(reader, "LastSyncTime");
        item.Reason = ReadNullableString(reader, "Reason");
        return item;
    }

    /// <inheritdoc />
    public override PeopleSyncItem? GetByKey(string key) => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM PeopleSyncItems WHERE PersonName = @name LIMIT 1";
            cmd.Parameters.AddWithValue("@name", key);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? MapFromReader(reader) : null;
        },
        fallback: null);

    /// <inheritdoc />
    public override void DeleteByKey(string key) => ExecuteWrite(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM PeopleSyncItems WHERE PersonName = @name";
        cmd.Parameters.AddWithValue("@name", key);
        cmd.ExecuteNonQuery();
    });

    /// <inheritdoc />
    public override void Upsert(PeopleSyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            // Ignored is sticky: an existing Ignored row is preserved against
            // automatic transitions from Refresh/Compare/Sync. Reason is also
            // preserved on Ignored rows so the user-entered reason isn't blown
            // away by the next cycle.
            cmd.CommandText = @"
                INSERT INTO PeopleSyncItems (
                    PersonName, SourcePersonId, LocalPersonId,
                    SourceMetadataValue, LocalMetadataValue,
                    SourceMetadataHash, SyncedMetadataHash,
                    SourceImagesValue, LocalImagesValue,
                    SourceImagesHash, SyncedImagesHash,
                    Status, StatusDate, LastSyncTime, Reason
                ) VALUES (
                    @name, @sourceId, @localId,
                    @srcMeta, @localMeta,
                    @srcMetaHash, @syncedMetaHash,
                    @srcImg, @localImg,
                    @srcImgHash, @syncedImgHash,
                    @status, @statusDate, @lastSync, @reason
                )
                ON CONFLICT(PersonName) DO UPDATE SET
                    SourcePersonId = @sourceId,
                    LocalPersonId = @localId,
                    SourceMetadataValue = @srcMeta,
                    LocalMetadataValue = @localMeta,
                    SourceMetadataHash = @srcMetaHash,
                    SyncedMetadataHash = CASE WHEN @syncedMetaHash IS NOT NULL THEN @syncedMetaHash ELSE PeopleSyncItems.SyncedMetadataHash END,
                    SourceImagesValue = @srcImg,
                    LocalImagesValue = @localImg,
                    SourceImagesHash = @srcImgHash,
                    SyncedImagesHash = CASE WHEN @syncedImgHash IS NOT NULL THEN @syncedImgHash ELSE PeopleSyncItems.SyncedImagesHash END,
                    Status = CASE WHEN PeopleSyncItems.Status = @ignoredStatus THEN @ignoredStatus ELSE @status END,
                    StatusDate = CASE WHEN PeopleSyncItems.Status = @ignoredStatus THEN PeopleSyncItems.StatusDate ELSE @statusDate END,
                    LastSyncTime = CASE WHEN PeopleSyncItems.Status = @ignoredStatus THEN PeopleSyncItems.LastSyncTime ELSE @lastSync END,
                    Reason = CASE WHEN PeopleSyncItems.Status = @ignoredStatus THEN PeopleSyncItems.Reason ELSE @reason END";

            cmd.Parameters.AddWithValue("@name", record.PersonName);
            AddNullable(cmd, "@sourceId", record.SourcePersonId);
            AddNullable(cmd, "@localId", record.LocalPersonId);
            AddNullable(cmd, "@srcMeta", record.Metadata.Source);
            AddNullable(cmd, "@localMeta", record.Metadata.Local);
            AddNullable(cmd, "@srcMetaHash", record.Metadata.SourceHash);
            AddNullable(cmd, "@syncedMetaHash", record.Metadata.SyncedHash);
            AddNullable(cmd, "@srcImg", record.Images.Source);
            AddNullable(cmd, "@localImg", record.Images.Local);
            AddNullable(cmd, "@srcImgHash", record.Images.SourceHash);
            AddNullable(cmd, "@syncedImgHash", record.Images.SyncedHash);
            cmd.Parameters.AddWithValue("@status", (int)record.Status);
            AddTimestamp(cmd, "@statusDate", record.StatusDate);
            AddNullableTimestamp(cmd, "@lastSync", record.LastSyncTime);
            AddNullable(cmd, "@reason", record.Reason);
            cmd.Parameters.AddWithValue("@ignoredStatus", (int)SyncStatus.Ignored);
            cmd.ExecuteNonQuery();
        });
    }

    /// <inheritdoc />
    public override PagedResult<PeopleSyncItem> Paginate(PaginationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        var skip = (page - 1) * pageSize;

        return ExecuteRead(
            conn =>
            {
                var conditions = new List<string>();
                if (!string.IsNullOrWhiteSpace(request.SearchTerm))
                {
                    conditions.Add("PersonName LIKE @search");
                }

                if (request.StatusFilter.HasValue)
                {
                    conditions.Add("Status = @status");
                }

                var whereClause = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : string.Empty;

                int totalCount;
                using (var countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = $"SELECT COUNT(*) FROM PeopleSyncItems {whereClause}";
                    if (!string.IsNullOrWhiteSpace(request.SearchTerm))
                    {
                        countCmd.Parameters.AddWithValue("@search", $"%{request.SearchTerm}%");
                    }

                    if (request.StatusFilter.HasValue)
                    {
                        countCmd.Parameters.AddWithValue("@status", (int)request.StatusFilter.Value);
                    }

                    totalCount = Convert.ToInt32(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                var items = new List<PeopleSyncItem>();
                using (var dataCmd = conn.CreateCommand())
                {
                    dataCmd.CommandText = $@"
                        SELECT * FROM PeopleSyncItems
                        {whereClause}
                        ORDER BY {StatusPriorityOrderBy}, PersonName ASC, Id ASC
                        LIMIT @take OFFSET @skip";
                    if (!string.IsNullOrWhiteSpace(request.SearchTerm))
                    {
                        dataCmd.Parameters.AddWithValue("@search", $"%{request.SearchTerm}%");
                    }

                    if (request.StatusFilter.HasValue)
                    {
                        dataCmd.Parameters.AddWithValue("@status", (int)request.StatusFilter.Value);
                    }

                    dataCmd.Parameters.AddWithValue("@take", pageSize);
                    dataCmd.Parameters.AddWithValue("@skip", skip);

                    using var reader = dataCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        items.Add(MapFromReader(reader));
                    }
                }

                return new PagedResult<PeopleSyncItem>
                {
                    Items = items,
                    TotalCount = totalCount,
                    Page = page,
                    PageSize = pageSize
                };
            },
            fallback: new PagedResult<PeopleSyncItem>
            {
                Items = Array.Empty<PeopleSyncItem>(),
                TotalCount = 0,
                Page = page,
                PageSize = pageSize
            });
    }
}
