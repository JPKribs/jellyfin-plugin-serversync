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
/// composite (SourceUserId, SourceItemId), one row per source-side
/// (user, item) pair, regardless of which local user it maps to (the local
/// side is informational, not part of the key).
/// </summary>
[PluginService(ServiceLifetime.Transient)]
public sealed class HistorySyncTableManager
    : SyncTableManagerBase<HistorySyncItem, (string SourceUserId, string SourceItemId)>
{
    private readonly IPluginConfigurationManager? _configManager;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public HistorySyncTableManager(
        ISyncDatabaseProvider databaseProvider,
        ILogger<HistorySyncTableManager> logger,
        IPluginConfigurationManager? configManager = null)
        : base(GetDatabase(databaseProvider), logger)
    {
        _configManager = configManager;
    }

    // Whether rows should count a source that is behind as work. Not stored on the row, since it is
    // a property of the current configuration, so every row read from the database is stamped with
    // the live setting. The dashboard and the sync task then see the same answer the refresh did.
    private bool Negotiating => _configManager is not null && Plugin.Instance is not null && _configManager.Configuration.HistorySyncNegotiate;

    private static SyncDatabase GetDatabase(ISyncDatabaseProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.Database;
    }

    /// <inheritdoc />
    protected override string TableName => "HistorySyncItems";

    // Sentinel, unused, since UpdateStatusByKey is overridden directly to
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

        item.NegotiatedIsPlayed = ReadNullableBool(reader, "NegotiatedIsPlayed");
        item.NegotiatedPlayCount = ReadNullableInt32(reader, "NegotiatedPlayCount");
        item.NegotiatedPlaybackPositionTicks = ReadNullableInt64(reader, "NegotiatedPlaybackPositionTicks");
        item.NegotiatedLastPlayedDate = ReadNullableDateTime(reader, "NegotiatedLastPlayedDate");
        item.NegotiatedIsFavorite = ReadNullableBool(reader, "NegotiatedIsFavorite");
        item.NegotiatedAt = ReadNullableDateTime(reader, "NegotiatedAt");
        item.NegotiateWithSource = Negotiating;

        item.LastSyncTime = ReadNullableDateTime(reader, "LastSyncTime");
        item.Reason = ReadNullableString(reader, "Reason");
        item.RetryCount = ReadNullableInt32(reader, "RetryCount") ?? 0;
        item.ServerKey = ReadNullableString(reader, "ServerKey");
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
        // The negotiated base never moves backwards. A refresh that read the row before a hint or a
        // negotiation recorded a newer base would otherwise write its older copy over it. The base only
        // changes to one agreed at least as recently, and a write that carries no base keeps the one held.
        ArgumentNullException.ThrowIfNull(record);
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
    /// Returns the rows one peer holds for a local item, across local users. Used when that peer
    /// reports agreement on an item so this server's own row for it can record the agreed base.
    /// </summary>
    /// <param name="serverKey">The peer's entry key.</param>
    /// <param name="localItemId">The local item id as stored, without dashes.</param>
    /// <returns>The rows.</returns>
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
    /// Returns all history rows for a given user mapping, regardless of
    /// library, used by the controller to scope queries by user.
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
    /// filter beyond what <see cref="Paginate"/> exposes, used by the
    /// admin UI which can scope the list to a single source user.
    /// </summary>
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
