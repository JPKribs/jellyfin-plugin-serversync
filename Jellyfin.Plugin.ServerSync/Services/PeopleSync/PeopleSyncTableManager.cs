#pragma warning disable CA2100 // SQL is internal/parameterized.
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services.Database;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.PeopleSync;

/// <summary>
/// Manages the People sync table, with one row for each person name.
/// </summary>
[PluginService(ServiceLifetime.Transient)]
public sealed class PeopleSyncTableManager : SyncTableManagerBase<PeopleSyncItem, string>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeopleSyncTableManager"/> class.
    /// </summary>
    /// <param name="databaseProvider">Gives the sync database.</param>
    /// <param name="logger">Writes log entries.</param>
    public PeopleSyncTableManager(ISyncDatabaseProvider databaseProvider, ILogger<PeopleSyncTableManager> logger)
        : base(databaseProvider, logger)
    {
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
        item.RetryCount = ReadNullableInt32(reader, "RetryCount") ?? 0;
        item.ServerKey = ReadNullableString(reader, "ServerKey");
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
        var upsert = new UpsertBuilder(TableName)
            .Key("PersonName", record.PersonName)
            .Set("SourcePersonId", record.SourcePersonId)
            .Set("LocalPersonId", record.LocalPersonId)
            .Set("SourceMetadataValue", record.Metadata.Source)
            .Set("LocalMetadataValue", record.Metadata.Local)
            .Set("SourceMetadataHash", record.Metadata.SourceHash)
            .SetUnlessNull("SyncedMetadataHash", record.Metadata.SyncedHash)
            .Set("SourceImagesValue", record.Images.Source)
            .Set("LocalImagesValue", record.Images.Local)
            .Set("SourceImagesHash", record.Images.SourceHash)
            .SetUnlessNull("SyncedImagesHash", record.Images.SyncedHash)
            .SyncColumns(record);
        ExecuteWrite(upsert.Execute);
    }

    /// <inheritdoc />
    public override PagedResult<PeopleSyncItem> Paginate(PaginationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        return SearchPaginated(request.SearchTerm, request.StatusFilter, (page - 1) * pageSize, pageSize);
    }

    /// <summary>
    /// Gets the rows starting at an offset, so a skip that is not a multiple of the page size still lands on the rows asked for.
    /// </summary>
    /// <param name="searchTerm">Text to find in the person name, or null.</param>
    /// <param name="status">Only rows with this status, or null.</param>
    /// <param name="skip">How many matching rows to pass over.</param>
    /// <param name="take">The most rows to return, capped at 200.</param>
    /// <returns>The page of rows.</returns>
    public PagedResult<PeopleSyncItem> SearchPaginated(string? searchTerm, SyncStatus? status, int skip, int take)
    {
        skip = Math.Max(0, skip);
        var pageSize = Math.Clamp(take, 1, 200);

        return ExecuteRead(
            conn =>
            {
                var conditions = new List<string>();
                if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    conditions.Add("PersonName LIKE @search");
                }

                if (status.HasValue)
                {
                    conditions.Add("Status = @status");
                }

                var whereClause = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : string.Empty;

                int totalCount;
                using (var countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = $"SELECT COUNT(*) FROM PeopleSyncItems {whereClause}";
                    if (!string.IsNullOrWhiteSpace(searchTerm))
                    {
                        countCmd.Parameters.AddWithValue("@search", $"%{searchTerm}%");
                    }

                    if (status.HasValue)
                    {
                        countCmd.Parameters.AddWithValue("@status", (int)status.Value);
                    }

                    totalCount = Convert.ToInt32(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                var items = new List<PeopleSyncItem>();
                using (var dataCmd = conn.CreateCommand())
                {
                    dataCmd.CommandText = PageQuery("PeopleSyncItems", whereClause, $"{StatusPriorityOrderBy}, PersonName ASC, Id ASC");
                    if (!string.IsNullOrWhiteSpace(searchTerm))
                    {
                        dataCmd.Parameters.AddWithValue("@search", $"%{searchTerm}%");
                    }

                    if (status.HasValue)
                    {
                        dataCmd.Parameters.AddWithValue("@status", (int)status.Value);
                    }

                    dataCmd.Parameters.AddWithValue("@take", pageSize);
                    dataCmd.Parameters.AddWithValue("@skip", skip);

                    using var reader = dataCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        items.Add(MapFromReader(reader));
                    }
                }

                return new PagedResult<PeopleSyncItem>(items, totalCount, skip, pageSize);
            },
            fallback: new PagedResult<PeopleSyncItem>(Array.Empty<PeopleSyncItem>(), 0, skip, pageSize));
    }
}
