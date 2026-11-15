#pragma warning disable CA2100 // SQL is internal/parameterized.
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Database;
using JPKribs.Jellyfin.Base;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.HistorySync;

/// <summary>
/// Manages the History sync table, with one row for each source user and source item.
/// The key has two columns, so every key route is overridden and <see cref="KeyColumn"/> goes unused.
/// </summary>
[PluginService(ServiceLifetime.Transient)]
public sealed class HistorySyncTableManager
    : SyncTableManagerBase<HistorySyncItem, (string SourceUserId, string SourceItemId)>
{
    private readonly IPluginConfigurationManager? _configManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="HistorySyncTableManager"/> class.
    /// </summary>
    /// <param name="databaseProvider">Gives the sync database.</param>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="configManager">Reads whether a source that is behind counts as work for each row read, or null.</param>
    public HistorySyncTableManager(
        ISyncDatabaseProvider databaseProvider,
        ILogger<HistorySyncTableManager> logger,
        IPluginConfigurationManager? configManager = null)
        : base(databaseProvider, logger)
    {
        _configManager = configManager;
    }

    /// <inheritdoc />
    protected override string TableName => "HistorySyncItems";

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

        item.NegotiatedIsPlayed = ReadNullableBool(reader, "NegotiatedIsPlayed");
        item.NegotiatedPlayCount = ReadNullableInt32(reader, "NegotiatedPlayCount");
        item.NegotiatedPlaybackPositionTicks = ReadNullableInt64(reader, "NegotiatedPlaybackPositionTicks");
        item.NegotiatedLastPlayedDate = ReadNullableDateTime(reader, "NegotiatedLastPlayedDate");
        item.NegotiatedIsFavorite = ReadNullableBool(reader, "NegotiatedIsFavorite");
        item.NegotiatedAt = ReadNullableDateTime(reader, "NegotiatedAt");

        item.LastSyncTime = ReadNullableDateTime(reader, "LastSyncTime");
        item.Reason = ReadNullableString(reader, "Reason");
        item.RetryCount = ReadNullableInt32(reader, "RetryCount") ?? 0;
        item.ServerKey = ReadNullableString(reader, "ServerKey");
        item.NegotiateWithSource = _configManager is not null && Plugin.Instance is not null
            && _configManager.Configuration.NegotiatesHistory(item.ServerKey, item.SourceLibraryId);
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

        // The agreed base never moves back, so a scan cannot overwrite a newer one.
        const string NewerBase = "@p_NegotiatedAt IS NOT NULL AND (HistorySyncItems.NegotiatedAt IS NULL OR @p_NegotiatedAt >= HistorySyncItems.NegotiatedAt)";
        var upsert = new UpsertBuilder(TableName)
            .Key("SourceUserId", record.SourceUserId)
            .Key("SourceItemId", record.SourceItemId)
            .Set("LocalUserId", record.LocalUserId)
            .Set("SourceLibraryId", record.SourceLibraryId)
            .Set("LocalLibraryId", record.LocalLibraryId)
            .Set("LocalItemId", record.LocalItemId)
            .Set("ItemName", record.ItemName)
            .Set("SourcePath", record.SourcePath)
            .Set("LocalPath", record.LocalPath)
            .Set("SourceIsPlayed", UpsertBuilder.Flag(record.SourceIsPlayed))
            .Set("SourcePlayCount", record.SourcePlayCount)
            .Set("SourcePlaybackPositionTicks", record.SourcePlaybackPositionTicks)
            .Set("SourceLastPlayedDate", UpsertBuilder.Time(record.SourceLastPlayedDate))
            .Set("SourceIsFavorite", UpsertBuilder.Flag(record.SourceIsFavorite))
            .Set("LocalIsPlayed", UpsertBuilder.Flag(record.LocalIsPlayed))
            .Set("LocalPlayCount", record.LocalPlayCount)
            .Set("LocalPlaybackPositionTicks", record.LocalPlaybackPositionTicks)
            .Set("LocalLastPlayedDate", UpsertBuilder.Time(record.LocalLastPlayedDate))
            .Set("LocalIsFavorite", UpsertBuilder.Flag(record.LocalIsFavorite))
            .Set("MergedIsPlayed", UpsertBuilder.Flag(record.MergedIsPlayed))
            .Set("MergedPlayCount", record.MergedPlayCount)
            .Set("MergedPlaybackPositionTicks", record.MergedPlaybackPositionTicks)
            .Set("MergedLastPlayedDate", UpsertBuilder.Time(record.MergedLastPlayedDate))
            .Set("MergedIsFavorite", UpsertBuilder.Flag(record.MergedIsFavorite))
            .SetWhen("NegotiatedIsPlayed", UpsertBuilder.Flag(record.NegotiatedIsPlayed), NewerBase)
            .SetWhen("NegotiatedPlayCount", record.NegotiatedPlayCount, NewerBase)
            .SetWhen("NegotiatedPlaybackPositionTicks", record.NegotiatedPlaybackPositionTicks, NewerBase)
            .SetWhen("NegotiatedLastPlayedDate", UpsertBuilder.Time(record.NegotiatedLastPlayedDate), NewerBase)
            .SetWhen("NegotiatedIsFavorite", UpsertBuilder.Flag(record.NegotiatedIsFavorite), NewerBase)
            .SetWhen("NegotiatedAt", UpsertBuilder.Time(record.NegotiatedAt), NewerBase)
            .Set("SourceStateHash", record.SourceState.SourceHash)
            .SetUnlessIgnoredOrNull("SyncedStateHash", record.SourceState.SyncedHash)
            .SyncColumns(record);
        ExecuteWrite(upsert.Execute);
    }

    /// <summary>
    /// Gets the rows one peer holds for a local item, across local users, so an agreed base can be recorded on each.
    /// </summary>
    /// <param name="serverKey">The key of the peer's server entry.</param>
    /// <param name="localItemId">The local item id as stored, without dashes.</param>
    /// <returns>The matching rows, or none.</returns>
    public IList<HistorySyncItem> GetByLocalItem(string serverKey, string localItemId) => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM HistorySyncItems WHERE ServerKey = @key AND LocalItemId = @item";
            cmd.Parameters.AddWithValue("@key", serverKey);
            cmd.Parameters.AddWithValue("@item", localItemId);
            return ReadAll(cmd);
        },
        Array.Empty<HistorySyncItem>());

    /// <summary>
    /// Gets every history row for one user mapping, across libraries.
    /// </summary>
    /// <param name="sourceUserId">The mapped user's id on the source.</param>
    /// <param name="localUserId">The mapped user's id on this server.</param>
    /// <returns>The user's history rows.</returns>
    public IList<HistorySyncItem> GetByUserMapping(string sourceUserId, string localUserId) => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM HistorySyncItems WHERE SourceUserId = @user AND LocalUserId = @local";
            cmd.Parameters.AddWithValue("@user", sourceUserId);
            cmd.Parameters.AddWithValue("@local", localUserId);
            return ReadAll(cmd);
        },
        fallback: Array.Empty<HistorySyncItem>());

    /// <summary>
    /// Gets one page of history rows, filtered by text, status, and source user.
    /// </summary>
    /// <param name="searchTerm">Text to find in the item name or source path, or null.</param>
    /// <param name="status">Only rows with this status, or null.</param>
    /// <param name="sourceUserId">Only this source user's rows, or null.</param>
    /// <param name="skip">How many matching rows to pass over.</param>
    /// <param name="take">The most rows to return.</param>
    /// <returns>The page of rows and the count of every matching row.</returns>
    public (IList<HistorySyncItem> Items, int TotalCount) SearchHistoryItemsPaginated(
        string? searchTerm = null,
        SyncStatus? status = null,
        string? sourceUserId = null,
        int skip = 0,
        int take = 50) => ExecuteRead<(IList<HistorySyncItem> Items, int TotalCount)>(
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
            dataCmd.CommandText = PageQuery("HistorySyncItems", whereClause, $"{StatusPriorityOrderBy}, ItemName ASC, Id ASC");
            BindFilters(dataCmd, searchTerm, status, sourceUserId);
            dataCmd.Parameters.AddWithValue("@take", take);
            dataCmd.Parameters.AddWithValue("@skip", skip);
            return (ReadAll(dataCmd), totalCount);
        },
        fallback: (Array.Empty<HistorySyncItem>(), 0));

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

    /// <summary>
    /// Reads a 0 or 1 column as a flag, or null when the column is empty.
    /// </summary>
    private static bool? ReadNullableBool(IDataRecord reader, string column)
    {
        var ord = reader.GetOrdinal(column);
        return reader.IsDBNull(ord) ? null : reader.GetInt32(ord) == 1;
    }

    /// <summary>
    /// Binds the parameters of each filter that is set.
    /// </summary>
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
