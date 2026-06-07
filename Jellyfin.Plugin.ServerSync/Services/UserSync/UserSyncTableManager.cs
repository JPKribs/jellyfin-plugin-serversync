#pragma warning disable CA2100 // SQL is internal/parameterized.
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using JPKribs.Jellyfin.Base;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Per-table manager for <see cref="UserSyncItem"/>. Replaces the old
/// <c>SyncDatabase.UserSync.cs</c> partial-class methods. Natural key is the
/// composite (SourceUserId, LocalUserId, PropertyCategory) — three rows per
/// user mapping (one each for Policy, Configuration, ProfileImage).
/// </summary>
[PluginService(ServiceLifetime.Transient)]
public sealed class UserSyncTableManager
    : SyncTableManagerBase<UserSyncItem, (string SourceUserId, string LocalUserId, string PropertyCategory)>
{
    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public UserSyncTableManager(ISyncDatabaseProvider databaseProvider, ILogger<UserSyncTableManager> logger)
        : base(GetDatabase(databaseProvider), logger)
    {
    }

    private static SyncDatabase GetDatabase(ISyncDatabaseProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.Database;
    }

    /// <inheritdoc />
    protected override string TableName => "UserSyncItems";

    // Sentinel — UpdateStatusByKey is overridden directly to handle the
    // composite (SourceUserId, LocalUserId, PropertyCategory) key.
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
    /// Counts distinct user mappings (SourceUserId, LocalUserId pairs) that
    /// have at least one category row in <paramref name="status"/>. A mapping
    /// with one category Queued and another Errored counts in both buckets,
    /// matching how the row is rendered by worst-case status.
    /// </summary>
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
    /// Returns all rows (all categories) for a given user mapping. Used by
    /// the user-grouping UI which displays all three categories per user as
    /// one logical row.
    /// </summary>
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
        fallback: (IList<UserSyncItem>)Array.Empty<UserSyncItem>());

    /// <summary>
    /// Returns one page of distinct user mappings, each with its grouped
    /// category items. Used by the user-grouping UI.
    /// </summary>
    public PagedResult<UserMappingGroup> PaginateUserMappings(PaginationRequest request)
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
                    conditions.Add("(SourceUserName LIKE @search OR LocalUserName LIKE @search)");
                }

                if (request.StatusFilter.HasValue)
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
                        mappings.Add((reader.GetString(0), reader.GetString(1)));
                    }
                }

                // Fetch all rows for the entire page in one query, then group
                // in memory. Avoids the previous N+1 (one GetByUserMapping per
                // mapping = up to 200 SELECTs holding the global write-lock).
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
    /// Updates status for all categories of multiple user mappings in one
    /// transaction. Returns the count of rows updated.
    /// </summary>
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
    /// Deletes all rows for a given user mapping (all three categories).
    /// Returns the number of rows deleted.
    /// </summary>
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
        ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO UserSyncItems (
                    SourceUserId, LocalUserId, SourceUserName, LocalUserName,
                    PropertyCategory,
                    SourceValue, LocalValue, MergedValue,
                    SourceValueHash, SyncedValueHash,
                    SourceImageHash, LocalImageHash, SyncedImageHash,
                    SourceImageSize, LocalImageSize, SyncedImageSize,
                    Status, StatusDate, LastSyncTime, Reason
                ) VALUES (
                    @src, @loc, @srcName, @locName,
                    @cat,
                    @srcVal, @locVal, @mergedVal,
                    @srcValHash, @syncedValHash,
                    @srcImgHash, @locImgHash, @syncedImgHash,
                    @srcImgSize, @locImgSize, @syncedImgSize,
                    @status, @statusDate, @lastSync, @reason
                )
                ON CONFLICT(SourceUserId, LocalUserId, PropertyCategory) DO UPDATE SET
                    SourceUserName = @srcName,
                    LocalUserName = @locName,
                    SourceValue = @srcVal,
                    LocalValue = @locVal,
                    MergedValue = @mergedVal,
                    SourceValueHash = @srcValHash,
                    SyncedValueHash = CASE WHEN @syncedValHash IS NOT NULL THEN @syncedValHash ELSE UserSyncItems.SyncedValueHash END,
                    SourceImageHash = @srcImgHash,
                    LocalImageHash = @locImgHash,
                    SyncedImageHash = CASE WHEN @syncedImgHash IS NOT NULL THEN @syncedImgHash ELSE UserSyncItems.SyncedImageHash END,
                    SourceImageSize = @srcImgSize,
                    LocalImageSize = @locImgSize,
                    SyncedImageSize = CASE WHEN @syncedImgSize IS NOT NULL THEN @syncedImgSize ELSE UserSyncItems.SyncedImageSize END,
                    Status = CASE WHEN UserSyncItems.Status = @ignoredStatus THEN @ignoredStatus ELSE @status END,
                    StatusDate = CASE WHEN UserSyncItems.Status = @ignoredStatus THEN UserSyncItems.StatusDate ELSE @statusDate END,
                    LastSyncTime = CASE WHEN UserSyncItems.Status = @ignoredStatus THEN UserSyncItems.LastSyncTime ELSE @lastSync END,
                    Reason = CASE WHEN UserSyncItems.Status = @ignoredStatus THEN UserSyncItems.Reason ELSE @reason END";

            cmd.Parameters.AddWithValue("@src", record.SourceUserId);
            cmd.Parameters.AddWithValue("@loc", record.LocalUserId);
            AddNullable(cmd, "@srcName", record.SourceUserName);
            AddNullable(cmd, "@locName", record.LocalUserName);
            cmd.Parameters.AddWithValue("@cat", record.PropertyCategory);
            AddNullable(cmd, "@srcVal", record.SourceValue);
            AddNullable(cmd, "@locVal", record.LocalValue);
            AddNullable(cmd, "@mergedVal", record.MergedValue);
            AddNullable(cmd, "@srcValHash", record.SourceValueHash);
            AddNullable(cmd, "@syncedValHash", record.SyncedValueHash);
            AddNullable(cmd, "@srcImgHash", record.SourceImageHash);
            AddNullable(cmd, "@locImgHash", record.LocalImageHash);
            AddNullable(cmd, "@syncedImgHash", record.SyncedImageHash);
            AddNullable(cmd, "@srcImgSize", record.SourceImageSize);
            AddNullable(cmd, "@locImgSize", record.LocalImageSize);
            AddNullable(cmd, "@syncedImgSize", record.SyncedImageSize);
            cmd.Parameters.AddWithValue("@status", (int)record.Status);
            AddTimestamp(cmd, "@statusDate", record.StatusDate);
            AddNullableTimestamp(cmd, "@lastSync", record.LastSyncTime);
            AddNullable(cmd, "@reason", record.Reason);
            cmd.Parameters.AddWithValue("@ignoredStatus", (int)SyncStatus.Ignored);
            cmd.ExecuteNonQuery();
        });
    }

    /// <inheritdoc />
    public override PagedResult<UserSyncItem> Paginate(PaginationRequest request)
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
                    conditions.Add("(SourceUserName LIKE @search OR LocalUserName LIKE @search)");
                }

                if (request.StatusFilter.HasValue)
                {
                    conditions.Add("Status = @status");
                }

                var whereClause = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : string.Empty;

                int totalCount;
                using (var countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = $"SELECT COUNT(*) FROM UserSyncItems {whereClause}";
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

                var items = new List<UserSyncItem>();
                using (var dataCmd = conn.CreateCommand())
                {
                    dataCmd.CommandText = $@"
                        SELECT * FROM UserSyncItems
                        {whereClause}
                        ORDER BY {StatusPriorityOrderBy}, SourceUserName ASC, PropertyCategory ASC, Id ASC
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

                return new PagedResult<UserSyncItem>(items, totalCount, skip, pageSize);
            },
            fallback: new PagedResult<UserSyncItem>(Array.Empty<UserSyncItem>(), 0, skip, pageSize));
    }
}

/// <summary>
/// One user mapping with its grouped category items, returned by
/// <see cref="UserSyncTableManager.PaginateUserMappings"/>. The user-grouping
/// UI displays all three categories (Policy, Configuration, ProfileImage)
/// per user as one logical row; this DTO is that shape.
/// </summary>
public sealed class UserMappingGroup
{
    /// <summary>
    /// Gets the source server user ID.
    /// </summary>
    public required string SourceUserId { get; init; }

    /// <summary>
    /// Gets the local server user ID.
    /// </summary>
    public required string LocalUserId { get; init; }

    /// <summary>
    /// Gets the source user's display name.
    /// </summary>
    public string? SourceUserName { get; init; }

    /// <summary>
    /// Gets the local user's display name.
    /// </summary>
    public string? LocalUserName { get; init; }

    /// <summary>
    /// Gets all category items for this mapping (typically up to 3:
    /// Policy, Configuration, ProfileImage).
    /// </summary>
    public required IList<UserSyncItem> Items { get; init; }
}
