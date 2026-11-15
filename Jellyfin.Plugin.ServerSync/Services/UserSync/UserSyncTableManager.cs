#pragma warning disable CA2100 // SQL is internal/parameterized.
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Services.Database;
using JPKribs.Jellyfin.Base;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.UserSync;

/// <summary>
/// Manages the Users sync table, with one row for each user mapping and each of Policy, Configuration, and ProfileImage.
/// The key has three columns, so every key route is overridden and <see cref="KeyColumn"/> goes unused.
/// </summary>
[PluginService(ServiceLifetime.Transient)]
public sealed class UserSyncTableManager
    : SyncTableManagerBase<UserSyncItem, (string SourceUserId, string LocalUserId, string PropertyCategory)>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UserSyncTableManager"/> class.
    /// </summary>
    /// <param name="databaseProvider">Gives the sync database.</param>
    /// <param name="logger">Writes log entries.</param>
    public UserSyncTableManager(ISyncDatabaseProvider databaseProvider, ILogger<UserSyncTableManager> logger)
        : base(databaseProvider, logger)
    {
    }

    /// <inheritdoc />
    protected override string TableName => "UserSyncItems";

    /// <inheritdoc />
    protected override string KeyColumn => "SourceUserId";

    /// <inheritdoc />
    protected override UserSyncItem MapFromReader(IDataRecord reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var item = new UserSyncItem
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            SourceUserId = reader.GetString(reader.GetOrdinal("SourceUserId")),
            LocalUserId = reader.GetString(reader.GetOrdinal("LocalUserId")),
            PropertyCategory = reader.GetString(reader.GetOrdinal("PropertyCategory")),
            Status = (SyncStatus)reader.GetInt32(reader.GetOrdinal("Status")),
            StatusDate = ReadDateTime(reader, "StatusDate")
        };

        item.SourceUserName = ReadNullableString(reader, "SourceUserName");
        item.LocalUserName = ReadNullableString(reader, "LocalUserName");
        item.SourceValue = ReadNullableString(reader, "SourceValue");
        item.LocalValue = ReadNullableString(reader, "LocalValue");
        item.MergedValue = ReadNullableString(reader, "MergedValue");
        item.SourceValueHash = ReadNullableString(reader, "SourceValueHash");
        item.SyncedValueHash = ReadNullableString(reader, "SyncedValueHash");
        item.SourceImageHash = ReadNullableString(reader, "SourceImageHash");
        item.LocalImageHash = ReadNullableString(reader, "LocalImageHash");
        item.SyncedImageHash = ReadNullableString(reader, "SyncedImageHash");
        item.SourceImageSize = ReadNullableInt64(reader, "SourceImageSize");
        item.LocalImageSize = ReadNullableInt64(reader, "LocalImageSize");
        item.SyncedImageSize = ReadNullableInt64(reader, "SyncedImageSize");

        item.LastSyncTime = ReadNullableDateTime(reader, "LastSyncTime");
        item.Reason = ReadNullableString(reader, "Reason");
        item.RetryCount = ReadNullableInt32(reader, "RetryCount") ?? 0;
        item.ServerKey = ReadNullableString(reader, "ServerKey");
        return item;
    }

    /// <inheritdoc />
    public override void UpdateStatusByKey(
        (string SourceUserId, string LocalUserId, string PropertyCategory) key,
        SyncStatus status,
        string? reason = null) => ExecuteWrite(conn =>
    {
        using var cmd = conn.CreateCommand();
        var clauses = BuildStatusTransitionClauses(status);
        cmd.CommandText = $@"
            UPDATE UserSyncItems SET {string.Join(", ", clauses)}
            WHERE SourceUserId = @src AND LocalUserId = @loc AND PropertyCategory = @cat";
        AddStatusTransitionParameters(cmd, status, reason);
        cmd.Parameters.AddWithValue("@src", key.SourceUserId);
        cmd.Parameters.AddWithValue("@loc", key.LocalUserId);
        cmd.Parameters.AddWithValue("@cat", key.PropertyCategory);
        cmd.ExecuteNonQuery();
    });

    /// <inheritdoc />
    public override UserSyncItem? GetByKey((string SourceUserId, string LocalUserId, string PropertyCategory) key) => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT * FROM UserSyncItems
                WHERE SourceUserId = @src AND LocalUserId = @loc AND PropertyCategory = @cat
                LIMIT 1";
            cmd.Parameters.AddWithValue("@src", key.SourceUserId);
            cmd.Parameters.AddWithValue("@loc", key.LocalUserId);
            cmd.Parameters.AddWithValue("@cat", key.PropertyCategory);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? MapFromReader(reader) : null;
        },
        fallback: null);

    /// <inheritdoc />
    public override void DeleteByKey((string SourceUserId, string LocalUserId, string PropertyCategory) key) => ExecuteWrite(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM UserSyncItems
            WHERE SourceUserId = @src AND LocalUserId = @loc AND PropertyCategory = @cat";
        cmd.Parameters.AddWithValue("@src", key.SourceUserId);
        cmd.Parameters.AddWithValue("@loc", key.LocalUserId);
        cmd.Parameters.AddWithValue("@cat", key.PropertyCategory);
        cmd.ExecuteNonQuery();
    });

    /// <summary>
    /// Counts the user mappings with at least one row in a status, so a mapping with rows in two statuses counts under both.
    /// </summary>
    /// <param name="status">The status a mapping needs a row in to be counted.</param>
    /// <returns>The number of user mappings.</returns>
    public int CountUserMappingsByStatus(SyncStatus status) => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(*) FROM (
                    SELECT DISTINCT SourceUserId, LocalUserId
                    FROM UserSyncItems
                    WHERE Status = @Status
                )";
            cmd.Parameters.AddWithValue("@Status", (int)status);
            var result = cmd.ExecuteScalar();
            return Convert.ToInt32(result, CultureInfo.InvariantCulture);
        },
        fallback: 0);

    /// <summary>
    /// Gets every category's row for one user mapping.
    /// </summary>
    /// <param name="sourceUserId">The mapped user's id on the source.</param>
    /// <param name="localUserId">The mapped user's id on this server.</param>
    /// <returns>The mapping's rows, ordered by category.</returns>
    public IList<UserSyncItem> GetByUserMapping(string sourceUserId, string localUserId) => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT * FROM UserSyncItems
                WHERE SourceUserId = @src AND LocalUserId = @loc
                ORDER BY PropertyCategory";
            cmd.Parameters.AddWithValue("@src", sourceUserId);
            cmd.Parameters.AddWithValue("@loc", localUserId);
            return ReadAll(cmd);
        },
        fallback: Array.Empty<UserSyncItem>());

    /// <summary>
    /// Gets one page of user mappings, each with the rows of its categories.
    /// </summary>
    /// <param name="request">The page, search, and status filter asked for.</param>
    /// <returns>The page of mappings.</returns>
    public PagedResult<UserMappingGroup> PaginateUserMappings(PaginationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        return SearchUserMappingsPaginated(request.SearchTerm, request.StatusFilter, (page - 1) * pageSize, pageSize);
    }

    /// <summary>
    /// Gets the user mappings starting at an offset, each with the rows of its categories.
    /// The page's rows come from one query and are grouped in memory, which keeps the database lock short.
    /// </summary>
    /// <param name="searchTerm">Text to find in either user name, or null.</param>
    /// <param name="status">Only mappings with a row in this status, or null.</param>
    /// <param name="skip">How many matching mappings to pass over.</param>
    /// <param name="take">The most mappings to return, capped at 200.</param>
    /// <returns>The page of mappings.</returns>
    public PagedResult<UserMappingGroup> SearchUserMappingsPaginated(string? searchTerm, SyncStatus? status, int skip, int take)
    {
        skip = Math.Max(0, skip);
        var pageSize = Math.Clamp(take, 1, 200);

        return ExecuteRead(
            conn =>
            {
                var conditions = new List<string>();
                if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    conditions.Add("(SourceUserName LIKE @search OR LocalUserName LIKE @search)");
                }

                if (status.HasValue)
                {
                    conditions.Add("Status = @status");
                }

                var whereClause = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : string.Empty;

                int totalCount;
                using (var countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = $@"
                        SELECT COUNT(DISTINCT SourceUserId || '|' || LocalUserId)
                        FROM UserSyncItems
                        {whereClause}";
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

                var mappings = new List<(string Src, string Loc)>();
                using (var dataCmd = conn.CreateCommand())
                {
                    dataCmd.CommandText = $@"
                        SELECT SourceUserId, LocalUserId
                        FROM UserSyncItems
                        {whereClause}
                        GROUP BY SourceUserId, LocalUserId
                        ORDER BY MIN(SourceUserName), MIN(LocalUserName), SourceUserId, LocalUserId
                        LIMIT @take OFFSET @skip";
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
                        mappings.Add((reader.GetString(0), reader.GetString(1)));
                    }
                }

                var groups = new List<UserMappingGroup>();
                if (mappings.Count == 0)
                {
                    return new PagedResult<UserMappingGroup>(groups, totalCount, skip, pageSize);
                }

                var rowsByMapping = new Dictionary<(string Src, string Loc), List<UserSyncItem>>(mappings.Count);
                foreach (var key in mappings)
                {
                    rowsByMapping[key] = new List<UserSyncItem>();
                }

                using (var pageCmd = conn.CreateCommand())
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("SELECT * FROM UserSyncItems WHERE ");
                    for (var i = 0; i < mappings.Count; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(" OR ");
                        }

                        sb.Append("(SourceUserId = @s").Append(i).Append(" AND LocalUserId = @l").Append(i).Append(')');
                        pageCmd.Parameters.AddWithValue($"@s{i}", mappings[i].Src);
                        pageCmd.Parameters.AddWithValue($"@l{i}", mappings[i].Loc);
                    }

                    sb.Append(" ORDER BY SourceUserId, LocalUserId, PropertyCategory");
                    pageCmd.CommandText = sb.ToString();

                    using var reader = pageCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var item = MapFromReader(reader);
                        var key = (item.SourceUserId, item.LocalUserId);
                        if (rowsByMapping.TryGetValue(key, out var list))
                        {
                            list.Add(item);
                        }
                    }
                }

                foreach (var (src, loc) in mappings)
                {
                    var items = rowsByMapping[(src, loc)];
                    groups.Add(new UserMappingGroup
                    {
                        SourceUserId = src,
                        LocalUserId = loc,
                        SourceUserName = items.Count > 0 ? items[0].SourceUserName : null,
                        LocalUserName = items.Count > 0 ? items[0].LocalUserName : null,
                        Items = items
                    });
                }

                return new PagedResult<UserMappingGroup>(groups, totalCount, skip, pageSize);
            },
            fallback: new PagedResult<UserMappingGroup>(Array.Empty<UserMappingGroup>(), 0, skip, pageSize));
    }

    /// <summary>
    /// Sets the status of every category of many user mappings in one transaction.
    /// </summary>
    /// <param name="mappings">The source and local user ids of each mapping to update.</param>
    /// <param name="status">The status to move each row to.</param>
    /// <param name="reason">The reason to store, for a status that carries one.</param>
    /// <returns>How many rows were updated.</returns>
    public int BulkUpdateStatusByMappings(
        IEnumerable<(string SourceUserId, string LocalUserId)> mappings,
        SyncStatus status,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        var updated = 0;
        ExecuteWrite(conn =>
        {
            using var transaction = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            var clauses = BuildStatusTransitionClauses(status);
            cmd.CommandText = $@"
                UPDATE UserSyncItems SET {string.Join(", ", clauses)}
                WHERE SourceUserId = @src AND LocalUserId = @loc";
            AddStatusTransitionParameters(cmd, status, reason);
            var srcParam = cmd.Parameters.Add("@src", SqliteType.Text);
            var locParam = cmd.Parameters.Add("@loc", SqliteType.Text);

            foreach (var (src, loc) in mappings)
            {
                srcParam.Value = src;
                locParam.Value = loc;
                updated += cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        });
        return updated;
    }

    /// <summary>
    /// Deletes every category's row for one user mapping.
    /// </summary>
    /// <param name="sourceUserId">The mapped user's id on the source.</param>
    /// <param name="localUserId">The mapped user's id on this server.</param>
    /// <returns>How many rows were deleted.</returns>
    public int DeleteByUserMapping(string sourceUserId, string localUserId)
    {
        var deleted = 0;
        ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM UserSyncItems
                WHERE SourceUserId = @src AND LocalUserId = @loc";
            cmd.Parameters.AddWithValue("@src", sourceUserId);
            cmd.Parameters.AddWithValue("@loc", localUserId);
            deleted = cmd.ExecuteNonQuery();
        });
        return deleted;
    }

    /// <inheritdoc />
    public override void Upsert(UserSyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var upsert = new UpsertBuilder(TableName)
            .Key("SourceUserId", record.SourceUserId)
            .Key("LocalUserId", record.LocalUserId)
            .Key("PropertyCategory", record.PropertyCategory)
            .Set("SourceUserName", record.SourceUserName)
            .Set("LocalUserName", record.LocalUserName)
            .Set("SourceValue", record.SourceValue)
            .Set("LocalValue", record.LocalValue)
            .Set("MergedValue", record.MergedValue)
            .Set("SourceValueHash", record.SourceValueHash)
            .SetUnlessNull("SyncedValueHash", record.SyncedValueHash)
            .Set("SourceImageHash", record.SourceImageHash)
            .Set("LocalImageHash", record.LocalImageHash)
            .SetUnlessNull("SyncedImageHash", record.SyncedImageHash)
            .Set("SourceImageSize", record.SourceImageSize)
            .Set("LocalImageSize", record.LocalImageSize)
            .SetUnlessNull("SyncedImageSize", record.SyncedImageSize)
            .SyncColumns(record);
        ExecuteWrite(upsert.Execute);
    }

    /// <inheritdoc />
    public override PagedResult<UserSyncItem> Paginate(PaginationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        return SearchPaginated(request.SearchTerm, request.StatusFilter, sourceUserId: null, propertyCategory: null, (page - 1) * pageSize, pageSize);
    }

    /// <summary>
    /// Gets the rows starting at an offset, with every filter applied in the query so each page is full and the total counts only matches.
    /// </summary>
    /// <param name="searchTerm">Text to find in either user name, or null.</param>
    /// <param name="status">Only rows with this status, or null.</param>
    /// <param name="sourceUserId">Only this source user's rows, matched without regard to case, or null.</param>
    /// <param name="propertyCategory">Only rows of this category, matched without regard to case, or null.</param>
    /// <param name="skip">How many matching rows to pass over.</param>
    /// <param name="take">The most rows to return, capped at 200.</param>
    /// <returns>The page of rows.</returns>
    public PagedResult<UserSyncItem> SearchPaginated(
        string? searchTerm,
        SyncStatus? status,
        string? sourceUserId,
        string? propertyCategory,
        int skip,
        int take)
    {
        skip = Math.Max(0, skip);
        var pageSize = Math.Clamp(take, 1, 200);

        return ExecuteRead(
            conn =>
            {
                var conditions = new List<string>();
                if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    conditions.Add("(SourceUserName LIKE @search OR LocalUserName LIKE @search)");
                }

                if (status.HasValue)
                {
                    conditions.Add("Status = @status");
                }

                if (!string.IsNullOrEmpty(sourceUserId))
                {
                    conditions.Add("SourceUserId = @sourceUserId COLLATE NOCASE");
                }

                if (!string.IsNullOrEmpty(propertyCategory))
                {
                    conditions.Add("PropertyCategory = @propertyCategory COLLATE NOCASE");
                }

                var whereClause = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : string.Empty;

                void BindFilters(SqliteCommand cmd)
                {
                    if (!string.IsNullOrWhiteSpace(searchTerm))
                    {
                        cmd.Parameters.AddWithValue("@search", $"%{searchTerm}%");
                    }

                    if (status.HasValue)
                    {
                        cmd.Parameters.AddWithValue("@status", (int)status.Value);
                    }

                    if (!string.IsNullOrEmpty(sourceUserId))
                    {
                        cmd.Parameters.AddWithValue("@sourceUserId", sourceUserId);
                    }

                    if (!string.IsNullOrEmpty(propertyCategory))
                    {
                        cmd.Parameters.AddWithValue("@propertyCategory", propertyCategory);
                    }
                }

                int totalCount;
                using (var countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = $"SELECT COUNT(*) FROM UserSyncItems {whereClause}";
                    BindFilters(countCmd);
                    totalCount = Convert.ToInt32(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                var items = new List<UserSyncItem>();
                using (var dataCmd = conn.CreateCommand())
                {
                    dataCmd.CommandText = PageQuery("UserSyncItems", whereClause, $"{StatusPriorityOrderBy}, SourceUserName ASC, PropertyCategory ASC, Id ASC");
                    BindFilters(dataCmd);
                    dataCmd.Parameters.AddWithValue("@take", pageSize);
                    dataCmd.Parameters.AddWithValue("@skip", skip);

                    using var reader = dataCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        items.Add(MapFromReader(reader));
                    }
                }

                return new PagedResult<UserSyncItem>(items, totalCount, skip, pageSize);
            },
            fallback: new PagedResult<UserSyncItem>(Array.Empty<UserSyncItem>(), 0, skip, pageSize));
    }
}
