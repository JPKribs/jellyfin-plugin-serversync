#pragma warning disable CA2100 // SQL is internal/parameterized.
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using JPKribs.Jellyfin.Base;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Per-table manager for <see cref="HistorySyncItem"/>. Natural key is the
/// composite (SourceUserId, SourceItemId) — one row per source-side
/// (user, item) pair, regardless of which local user it maps to (the local
/// side is informational, not part of the key).
/// </summary>
[PluginService(ServiceLifetime.Transient)]
public sealed class HistorySyncTableManager
    : SyncTableManagerBase<HistorySyncItem, (string SourceUserId, string SourceItemId)>
{
    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public HistorySyncTableManager(ISyncDatabaseProvider databaseProvider, ILogger<HistorySyncTableManager> logger)
        : base(GetDatabase(databaseProvider), logger)
    {
    }

    private static SyncDatabase GetDatabase(ISyncDatabaseProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.Database;
    }

    /// <inheritdoc />
    protected override string TableName => "HistorySyncItems";

    // Sentinel — unused, since UpdateStatusByKey is overridden directly to
    // handle the composite (SourceUserId, SourceItemId) key.
    /// <inheritdoc />
    protected override string KeyColumn => "SourceItemId";

    // History has no Pending status: rows are either Queued or Synced.
    // Order: Errored, Queued, Ignored, Synced.
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
    protected override HistorySyncItem MapFromReader(IDataRecord reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var item = new HistorySyncItem
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            SourceUserId = reader.GetString(reader.GetOrdinal("SourceUserId")),
            LocalUserId = reader.GetString(reader.GetOrdinal("LocalUserId")),
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

        item.SourceIsPlayed = ReadNullableBool(reader, "SourceIsPlayed");
        item.SourcePlayCount = ReadNullableInt32(reader, "SourcePlayCount");
        item.SourcePlaybackPositionTicks = ReadNullableInt64(reader, "SourcePlaybackPositionTicks");
        item.SourceLastPlayedDate = ReadNullableDateTime(reader, "SourceLastPlayedDate");
        item.SourceIsFavorite = ReadNullableBool(reader, "SourceIsFavorite");

        item.LocalIsPlayed = ReadNullableBool(reader, "LocalIsPlayed");
        item.LocalPlayCount = ReadNullableInt32(reader, "LocalPlayCount");
        item.LocalPlaybackPositionTicks = ReadNullableInt64(reader, "LocalPlaybackPositionTicks");
        item.LocalLastPlayedDate = ReadNullableDateTime(reader, "LocalLastPlayedDate");
        item.LocalIsFavorite = ReadNullableBool(reader, "LocalIsFavorite");

        item.MergedIsPlayed = ReadNullableBool(reader, "MergedIsPlayed");
        item.MergedPlayCount = ReadNullableInt32(reader, "MergedPlayCount");
        item.MergedPlaybackPositionTicks = ReadNullableInt64(reader, "MergedPlaybackPositionTicks");
        item.MergedLastPlayedDate = ReadNullableDateTime(reader, "MergedLastPlayedDate");
        item.MergedIsFavorite = ReadNullableBool(reader, "MergedIsFavorite");

        item.LastSyncTime = ReadNullableDateTime(reader, "LastSyncTime");
        item.Reason = ReadNullableString(reader, "Reason");
        item.SourceState.SourceHash = ReadNullableString(reader, "SourceStateHash");
        item.SourceState.SyncedHash = ReadNullableString(reader, "SyncedStateHash");
        return item;
    }

    /// <inheritdoc />
    public override HistorySyncItem? GetByKey((string SourceUserId, string SourceItemId) key) => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM HistorySyncItems WHERE SourceUserId = @user AND SourceItemId = @item LIMIT 1";
            cmd.Parameters.AddWithValue("@user", key.SourceUserId);
            cmd.Parameters.AddWithValue("@item", key.SourceItemId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? MapFromReader(reader) : null;
        },
        fallback: null);

    /// <inheritdoc />
    public override void DeleteByKey((string SourceUserId, string SourceItemId) key) => ExecuteWrite(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM HistorySyncItems WHERE SourceUserId = @user AND SourceItemId = @item";
        cmd.Parameters.AddWithValue("@user", key.SourceUserId);
        cmd.Parameters.AddWithValue("@item", key.SourceItemId);
        cmd.ExecuteNonQuery();
    });

    /// <inheritdoc />
    public override void UpdateStatusByKey(
        (string SourceUserId, string SourceItemId) key,
        SyncStatus status,
        string? reason = null) => ExecuteWrite(conn =>
    {
        using var cmd = conn.CreateCommand();
        var clauses = BuildStatusTransitionClauses(status);
        cmd.CommandText = $@"
            UPDATE HistorySyncItems SET {string.Join(", ", clauses)}
            WHERE SourceUserId = @user AND SourceItemId = @item";
        AddStatusTransitionParameters(cmd, status, reason);
        cmd.Parameters.AddWithValue("@user", key.SourceUserId);
        cmd.Parameters.AddWithValue("@item", key.SourceItemId);
        cmd.ExecuteNonQuery();
    });

    /// <inheritdoc />
    public override void Upsert(HistorySyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO HistorySyncItems (
                    SourceUserId, LocalUserId, SourceLibraryId, LocalLibraryId,
                    SourceItemId, LocalItemId, ItemName, SourcePath, LocalPath,
                    SourceIsPlayed, SourcePlayCount, SourcePlaybackPositionTicks, SourceLastPlayedDate, SourceIsFavorite,
                    LocalIsPlayed, LocalPlayCount, LocalPlaybackPositionTicks, LocalLastPlayedDate, LocalIsFavorite,
                    MergedIsPlayed, MergedPlayCount, MergedPlaybackPositionTicks, MergedLastPlayedDate, MergedIsFavorite,
                    Status, StatusDate, LastSyncTime, Reason,
                    SourceStateHash, SyncedStateHash
                ) VALUES (
                    @sourceUserId, @localUserId, @sourceLibraryId, @localLibraryId,
                    @sourceItemId, @localItemId, @itemName, @sourcePath, @localPath,
                    @srcPlayed, @srcCount, @srcPos, @srcLast, @srcFav,
                    @locPlayed, @locCount, @locPos, @locLast, @locFav,
                    @mrgPlayed, @mrgCount, @mrgPos, @mrgLast, @mrgFav,
                    @status, @statusDate, @lastSync, @reason,
                    @srcStateHash, @syncedStateHash
                )
                ON CONFLICT(SourceUserId, SourceItemId) DO UPDATE SET
                    LocalUserId = @localUserId,
                    SourceLibraryId = @sourceLibraryId,
                    LocalLibraryId = @localLibraryId,
                    LocalItemId = @localItemId,
                    ItemName = @itemName,
                    SourcePath = @sourcePath,
                    LocalPath = @localPath,
                    SourceIsPlayed = @srcPlayed,
                    SourcePlayCount = @srcCount,
                    SourcePlaybackPositionTicks = @srcPos,
                    SourceLastPlayedDate = @srcLast,
                    SourceIsFavorite = @srcFav,
                    LocalIsPlayed = @locPlayed,
                    LocalPlayCount = @locCount,
                    LocalPlaybackPositionTicks = @locPos,
                    LocalLastPlayedDate = @locLast,
                    LocalIsFavorite = @locFav,
                    MergedIsPlayed = @mrgPlayed,
                    MergedPlayCount = @mrgCount,
                    MergedPlaybackPositionTicks = @mrgPos,
                    MergedLastPlayedDate = @mrgLast,
                    MergedIsFavorite = @mrgFav,
                    Status = CASE WHEN HistorySyncItems.Status = @ignoredStatus THEN @ignoredStatus ELSE @status END,
                    StatusDate = CASE WHEN HistorySyncItems.Status = @ignoredStatus THEN HistorySyncItems.StatusDate ELSE @statusDate END,
                    LastSyncTime = CASE WHEN HistorySyncItems.Status = @ignoredStatus THEN HistorySyncItems.LastSyncTime ELSE @lastSync END,
                    Reason = CASE WHEN HistorySyncItems.Status = @ignoredStatus THEN HistorySyncItems.Reason ELSE @reason END,
                    SourceStateHash = @srcStateHash,
                    SyncedStateHash = CASE WHEN HistorySyncItems.Status = @ignoredStatus THEN HistorySyncItems.SyncedStateHash ELSE @syncedStateHash END";

            cmd.Parameters.AddWithValue("@sourceUserId", record.SourceUserId);
            cmd.Parameters.AddWithValue("@localUserId", record.LocalUserId);
            cmd.Parameters.AddWithValue("@sourceLibraryId", record.SourceLibraryId);
            cmd.Parameters.AddWithValue("@localLibraryId", record.LocalLibraryId);
            cmd.Parameters.AddWithValue("@sourceItemId", record.SourceItemId);
            AddNullable(cmd, "@localItemId", record.LocalItemId);
            AddNullable(cmd, "@itemName", record.ItemName);
            AddNullable(cmd, "@sourcePath", record.SourcePath);
            AddNullable(cmd, "@localPath", record.LocalPath);
            AddNullableBool(cmd, "@srcPlayed", record.SourceIsPlayed);
            AddNullable(cmd, "@srcCount", record.SourcePlayCount);
            AddNullable(cmd, "@srcPos", record.SourcePlaybackPositionTicks);
            AddNullableTimestamp(cmd, "@srcLast", record.SourceLastPlayedDate);
            AddNullableBool(cmd, "@srcFav", record.SourceIsFavorite);
            AddNullableBool(cmd, "@locPlayed", record.LocalIsPlayed);
            AddNullable(cmd, "@locCount", record.LocalPlayCount);
            AddNullable(cmd, "@locPos", record.LocalPlaybackPositionTicks);
            AddNullableTimestamp(cmd, "@locLast", record.LocalLastPlayedDate);
            AddNullableBool(cmd, "@locFav", record.LocalIsFavorite);
            AddNullableBool(cmd, "@mrgPlayed", record.MergedIsPlayed);
            AddNullable(cmd, "@mrgCount", record.MergedPlayCount);
            AddNullable(cmd, "@mrgPos", record.MergedPlaybackPositionTicks);
            AddNullableTimestamp(cmd, "@mrgLast", record.MergedLastPlayedDate);
            AddNullableBool(cmd, "@mrgFav", record.MergedIsFavorite);
            cmd.Parameters.AddWithValue("@status", (int)record.Status);
            AddTimestamp(cmd, "@statusDate", record.StatusDate);
            AddNullableTimestamp(cmd, "@lastSync", record.LastSyncTime);
            AddNullable(cmd, "@reason", record.Reason);
            AddNullable(cmd, "@srcStateHash", record.SourceState.SourceHash);
            AddNullable(cmd, "@syncedStateHash", record.SourceState.SyncedHash);
            cmd.Parameters.AddWithValue("@ignoredStatus", (int)SyncStatus.Ignored);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Returns all history rows for a given user mapping, regardless of
    /// library — used by the controller to scope queries by user.
    /// </summary>
    public IList<HistorySyncItem> GetByUserMapping(string sourceUserId, string localUserId) => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM HistorySyncItems WHERE SourceUserId = @user AND LocalUserId = @local";
            cmd.Parameters.AddWithValue("@user", sourceUserId);
            cmd.Parameters.AddWithValue("@local", localUserId);
            return ReadAll(cmd);
        },
        fallback: (IList<HistorySyncItem>)Array.Empty<HistorySyncItem>());

    /// <summary>
    /// Searches history items with optional filters. Adds a SourceUserId
    /// filter beyond what <see cref="Paginate"/> exposes — used by the
    /// admin UI which can scope the list to a single source user.
    /// </summary>
    public (IList<HistorySyncItem> Items, int TotalCount) SearchHistoryItemsPaginated(
        string? searchTerm = null,
        SyncStatus? status = null,
        string? sourceUserId = null,
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

            if (!string.IsNullOrWhiteSpace(sourceUserId))
            {
                conditions.Add("SourceUserId = @sourceUserId");
            }

            var whereClause = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : string.Empty;

            int totalCount;
            using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = $"SELECT COUNT(*) FROM HistorySyncItems {whereClause}";
                BindFilters(countCmd, searchTerm, status, sourceUserId);
                totalCount = Convert.ToInt32(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            using var dataCmd = conn.CreateCommand();
            dataCmd.CommandText = $@"
                SELECT * FROM HistorySyncItems
                {whereClause}
                ORDER BY {StatusPriorityOrderBy}, ItemName ASC, Id ASC
                LIMIT @take OFFSET @skip";
            BindFilters(dataCmd, searchTerm, status, sourceUserId);
            dataCmd.Parameters.AddWithValue("@take", take);
            dataCmd.Parameters.AddWithValue("@skip", skip);
            return (ReadAll(dataCmd), totalCount);
        },
        fallback: ((IList<HistorySyncItem>)Array.Empty<HistorySyncItem>(), 0));

    /// <inheritdoc />
    public override PagedResult<HistorySyncItem> Paginate(PaginationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        var skip = (page - 1) * pageSize;
        var (items, total) = SearchHistoryItemsPaginated(request.SearchTerm, request.StatusFilter, sourceUserId: null, skip, pageSize);
        return new PagedResult<HistorySyncItem>((IReadOnlyList<HistorySyncItem>)items, total, skip, pageSize);
    }

    private static bool? ReadNullableBool(IDataRecord reader, string column)
    {
        var ord = reader.GetOrdinal(column);
        return reader.IsDBNull(ord) ? null : reader.GetInt32(ord) == 1;
    }

    private static void AddNullableBool(SqliteCommand cmd, string name, bool? value)
        => cmd.Parameters.AddWithValue(name, value.HasValue ? (value.Value ? 1 : 0) : (object)DBNull.Value);

    private static void BindFilters(SqliteCommand cmd, string? searchTerm, SyncStatus? status, string? sourceUserId)
    {
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            cmd.Parameters.AddWithValue("@search", $"%{searchTerm}%");
        }

        if (status.HasValue)
        {
            cmd.Parameters.AddWithValue("@status", (int)status.Value);
        }

        if (!string.IsNullOrWhiteSpace(sourceUserId))
        {
            cmd.Parameters.AddWithValue("@sourceUserId", sourceUserId);
        }
    }
}
