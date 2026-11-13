#pragma warning disable CA2100 // SQL is internal/parameterized.
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using JPKribs.Jellyfin.Base;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Per-table manager for content <see cref="SyncItem"/> records. Natural key
/// is <see cref="SyncItem.SourceItemId"/>. Adds Content-specific surface
/// (PendingType-aware queries, retry-count tracking, path collision checks,
/// sync stats) on top of the universal <see cref="SyncTableManagerBase{TRecord, TKey}"/>.
/// </summary>
[PluginService(ServiceLifetime.Transient)]
public sealed class ContentSyncTableManager : SyncTableManagerBase<SyncItem, string>
{
    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public ContentSyncTableManager(ISyncDatabaseProvider databaseProvider, ILogger<ContentSyncTableManager> logger)
        : base(GetDatabase(databaseProvider), logger)
    {
    }

    private static SyncDatabase GetDatabase(ISyncDatabaseProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.Database;
    }

    /// <inheritdoc />
    protected override string TableName => "SyncItems";

    /// <inheritdoc />
    protected override string KeyColumn => "SourceItemId";

    // Pending sorts first so the approval-workflow UX surfaces new items
    // awaiting user decision before everything else.
    /// <inheritdoc />
    protected override string StatusPriorityOrderBy => @"
        CASE Status
            WHEN 0 THEN 0
            WHEN 3 THEN 1
            WHEN 1 THEN 2
            WHEN 5 THEN 3
            WHEN 4 THEN 4
            WHEN 2 THEN 5
            ELSE 6
        END";

    /// <inheritdoc />
    protected override SyncItem MapFromReader(IDataRecord reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var item = new SyncItem
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            SourceLibraryId = reader.GetString(reader.GetOrdinal("SourceLibraryId")),
            LocalLibraryId = reader.GetString(reader.GetOrdinal("LocalLibraryId")),
            SourceItemId = reader.GetString(reader.GetOrdinal("SourceItemId")),
            SourcePath = reader.GetString(reader.GetOrdinal("SourcePath")),
            SourceSize = reader.GetInt64(reader.GetOrdinal("SourceSize")),
            SourceCreateDate = ReadDateTime(reader, "SourceCreateDate"),
            LocalItemId = ReadNullableString(reader, "LocalItemId"),
            LocalPath = ReadNullableString(reader, "LocalPath"),
            StatusDate = ReadDateTime(reader, "StatusDate"),
            Status = (SyncStatus)reader.GetInt32(reader.GetOrdinal("Status"))
        };

        var pendingTypeOrd = reader.GetOrdinal("PendingType");
        if (!reader.IsDBNull(pendingTypeOrd))
        {
            item.PendingType = (PendingType)reader.GetInt32(pendingTypeOrd);
        }

        item.LastSyncTime = ReadNullableDateTime(reader, "LastSyncTime");
        item.Reason = ReadNullableString(reader, "Reason");
        item.RetryCount = ReadNullableInt32(reader, "RetryCount") ?? 0;
        item.ServerKey = ReadNullableString(reader, "ServerKey");
        item.CompanionFiles = ReadNullableString(reader, "CompanionFiles");
        item.StorageMode = (StorageMode)(ReadNullableInt32(reader, "StorageMode") ?? 0);
        item.Kept = (KeptReason)(ReadNullableInt32(reader, "Kept") ?? 0);
        return item;
    }

    /// <inheritdoc />
    public override SyncItem? GetByKey(string key) => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM SyncItems WHERE SourceItemId = @key LIMIT 1";
            cmd.Parameters.AddWithValue("@key", key);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? MapFromReader(reader) : null;
        },
        fallback: null);

    /// <inheritdoc />
    public override void DeleteByKey(string key) => ExecuteWrite(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM SyncItems WHERE SourceItemId = @key";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.ExecuteNonQuery();
    });

    /// <inheritdoc />
    public override void Upsert(SyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var upsert = new UpsertBuilder(TableName)
            .Key("SourceItemId", record.SourceItemId)
            .Set("SourceLibraryId", record.SourceLibraryId)
            .Set("LocalLibraryId", record.LocalLibraryId)
            .Set("SourcePath", record.SourcePath)
            .Set("SourceSize", record.SourceSize)
            .Set("SourceCreateDate", UpsertBuilder.Time(record.SourceCreateDate))
            .Set("LocalItemId", record.LocalItemId)
            .Set("LocalPath", record.LocalPath)
            .SetUnlessIgnored("PendingType", record.PendingType.HasValue ? (int)record.PendingType.Value : null)
            .Set("CompanionFiles", record.CompanionFiles)
            .Set("StorageMode", (int)record.StorageMode)
            .Set("Kept", (int)record.Kept)
            .SyncColumns(record);
        ExecuteWrite(upsert.Execute);
    }

    /// <summary>
    /// Convenience alias for <see cref="GetByKey"/> matching the
    /// historical method name used widely in callers.
    /// </summary>
    public SyncItem? GetBySourceItemId(string sourceItemId) => GetByKey(sourceItemId);

    /// <summary>
    /// Returns the sync item with the given local path, or null if none.
    /// </summary>
    public SyncItem? GetByLocalPath(string localPath) => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM SyncItems WHERE LocalPath = @path LIMIT 1";
            cmd.Parameters.AddWithValue("@path", localPath);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? MapFromReader(reader) : null;
        },
        fallback: null);

    /// <summary>
    /// Marks the database as readable only by builds that know links, once it holds one. An older build
    /// then sets it aside instead of downloading media over a .strm it takes for a broken copy.
    /// </summary>
    public void RequireLinkAwareReaders() => ExecuteWrite(conn =>
    {
        var floor = DatabaseMigrationService.MinReaderFor(conn);
        if ((DatabaseSchema.GetMinReaderVersion(conn) ?? 0) < floor)
        {
            DatabaseSchema.Stamp(conn, Math.Max(floor, DatabaseSchema.GetVersion(conn)), floor);
        }
    });

    /// <summary>
    /// Returns every link row with a local path, and throws when the table cannot be read. The link
    /// cache drops the files of links that are gone, so it must never take a failed read for no links.
    /// </summary>
    public IList<SyncItem> GetLinksStrict() => ReadStrict(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM SyncItems WHERE StorageMode IN (1, 2) AND LocalPath IS NOT NULL";
        return ReadAll(cmd);
    });

    /// <summary>
    /// Returns every link row with a local path. The link registry reads this as one snapshot.
    /// </summary>
    public IList<SyncItem> GetLinks() => ExecuteRead<IList<SyncItem>>(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM SyncItems WHERE StorageMode IN (1, 2) AND LocalPath IS NOT NULL";
            return ReadAll(cmd);
        },
        fallback: new List<SyncItem>());

    /// <summary>
    /// Returns every link row with a local path, or null when the table could not be read. A failed read
    /// is not an empty list: a caller that took it for one would treat every link as a plain file.
    /// </summary>
    /// <returns>The link rows, or null.</returns>
    public IList<SyncItem>? TryGetLinks() => ExecuteRead<IList<SyncItem>?>(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM SyncItems WHERE StorageMode IN (1, 2) AND LocalPath IS NOT NULL";
            return ReadAll(cmd);
        },
        fallback: null);

    /// <summary>
    /// Returns true if another item already uses this local path.
    /// </summary>
    /// <param name="localPath">Path to check.</param>
    /// <param name="excludeSourceItemId">Optional item ID to exclude from check.</param>
    public bool CheckPathCollision(string localPath, string? excludeSourceItemId = null) => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            if (excludeSourceItemId != null)
            {
                cmd.CommandText = "SELECT COUNT(*) FROM SyncItems WHERE LocalPath = @path AND SourceItemId != @excludeId";
                cmd.Parameters.AddWithValue("@excludeId", excludeSourceItemId);
            }
            else
            {
                cmd.CommandText = "SELECT COUNT(*) FROM SyncItems WHERE LocalPath = @path";
            }

            cmd.Parameters.AddWithValue("@path", localPath);
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
        },
        fallback: false);

    /// <summary>
    /// Convenience alias for <see cref="DeleteByKey"/>.
    /// </summary>
    public void Delete(string sourceItemId) => DeleteByKey(sourceItemId);

    /// <summary>
    /// Searches sync items with pagination. Used by the Content admin UI
    /// which can additionally filter by <see cref="PendingType"/>, and to a set of
    /// items such as the links in the cache, beyond what the universal
    /// <see cref="Paginate"/> exposes.
    /// </summary>
    public (IList<SyncItem> Items, int TotalCount) SearchPaginated(
        string? searchTerm,
        SyncStatus? status = null,
        PendingType? pendingType = null,
        int skip = 0,
        int take = 50,
        IReadOnlyList<string>? sourceItemIds = null,
        bool? links = null) => ExecuteRead<(IList<SyncItem> Items, int TotalCount)>(
        conn =>
        {
            var conditions = new List<string>();
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                conditions.Add("(SourcePath LIKE @search OR LocalPath LIKE @search)");
            }

            if (status.HasValue)
            {
                conditions.Add("Status = @status");
            }

            if (pendingType.HasValue)
            {
                conditions.Add("PendingType = @pendingType");
            }

            // Links, including those on their way to copies, or copies, including those on their way back.
            if (links is { } wantLinks)
            {
                conditions.Add(wantLinks ? "StorageMode IN (1, 2)" : "StorageMode IN (0, 3)");
            }

            if (sourceItemIds is not null)
            {
                conditions.Add(sourceItemIds.Count == 0 ? "0" : $"SourceItemId IN ({string.Join(", ", sourceItemIds.Select((_, i) => "@id" + i.ToString(CultureInfo.InvariantCulture)))})");
            }

            var whereClause = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : string.Empty;

            int totalCount;
            using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = $"SELECT COUNT(*) FROM SyncItems {whereClause}";
                BindSearchFilters(countCmd, searchTerm, status, pendingType, sourceItemIds);
                totalCount = Convert.ToInt32(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            using var dataCmd = conn.CreateCommand();
            dataCmd.CommandText = $@"
                SELECT * FROM SyncItems
                {whereClause}
                ORDER BY {StatusPriorityOrderBy}, LocalPath ASC, Id ASC
                LIMIT @take OFFSET @skip";
            BindSearchFilters(dataCmd, searchTerm, status, pendingType, sourceItemIds);
            dataCmd.Parameters.AddWithValue("@take", take);
            dataCmd.Parameters.AddWithValue("@skip", skip);
            return (ReadAll(dataCmd), totalCount);
        },
        fallback: (Array.Empty<SyncItem>(), 0));

    /// <summary>
    /// The copies one server's Link mappings hold, kept by a pick, by hand, or from before, and those on
    /// their way back to links, a page at a time.
    /// </summary>
    /// <param name="serverKey">The server's entry key.</param>
    /// <param name="sourceLibraryIds">The server's libraries mapped as Link.</param>
    /// <param name="searchTerm">Text to find in the paths, or null.</param>
    /// <param name="skip">Rows to skip.</param>
    /// <param name="take">Rows to return.</param>
    /// <returns>The page and the total.</returns>
    public (IList<SyncItem> Items, int TotalCount) SearchKeptCopies(string serverKey, IReadOnlyList<string> sourceLibraryIds, string? searchTerm, int skip, int take)
    {
        ArgumentNullException.ThrowIfNull(sourceLibraryIds);
        if (sourceLibraryIds.Count == 0)
        {
            return (Array.Empty<SyncItem>(), 0);
        }

        return ExecuteRead<(IList<SyncItem> Items, int TotalCount)>(
            conn =>
            {
                var libraries = string.Join(", ", sourceLibraryIds.Select((_, i) => "@lib" + i.ToString(CultureInfo.InvariantCulture)));
                var where = $"WHERE StorageMode IN (0, 3) AND Status IN (@synced, @queued, @errored) AND ServerKey = @server AND SourceLibraryId IN ({libraries})"
                    + (string.IsNullOrWhiteSpace(searchTerm) ? string.Empty : " AND (SourcePath LIKE @search OR LocalPath LIKE @search)");

                void Bind(SqliteCommand cmd)
                {
                    cmd.Parameters.AddWithValue("@server", serverKey);
                    cmd.Parameters.AddWithValue("@synced", (int)SyncStatus.Synced);
                    cmd.Parameters.AddWithValue("@queued", (int)SyncStatus.Queued);
                    cmd.Parameters.AddWithValue("@errored", (int)SyncStatus.Errored);
                    for (var i = 0; i < sourceLibraryIds.Count; i++)
                    {
                        cmd.Parameters.AddWithValue("@lib" + i.ToString(CultureInfo.InvariantCulture), sourceLibraryIds[i]);
                    }

                    if (!string.IsNullOrWhiteSpace(searchTerm))
                    {
                        cmd.Parameters.AddWithValue("@search", "%" + searchTerm + "%");
                    }
                }

                int total;
                using (var count = conn.CreateCommand())
                {
                    count.CommandText = $"SELECT COUNT(*) FROM SyncItems {where}";
                    Bind(count);
                    total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                using var data = conn.CreateCommand();
                data.CommandText = $"SELECT * FROM SyncItems {where} ORDER BY LocalPath ASC, Id ASC LIMIT @take OFFSET @skip";
                Bind(data);
                data.Parameters.AddWithValue("@take", take);
                data.Parameters.AddWithValue("@skip", skip);
                return (ReadAll(data), total);
            },
            fallback: (Array.Empty<SyncItem>(), 0));
    }

    /// <inheritdoc />
    public override PagedResult<SyncItem> Paginate(PaginationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        var skip = (page - 1) * pageSize;
        var (items, total) = SearchPaginated(request.SearchTerm, request.StatusFilter, pendingType: null, skip, pageSize);
        return new PagedResult<SyncItem>((IReadOnlyList<SyncItem>)items, total, skip, pageSize);
    }

    /// <summary>
    /// Returns counts of Pending rows grouped by <see cref="PendingType"/>.
    /// </summary>
    public Dictionary<PendingType, int> GetPendingCounts() => ExecuteRead(
        conn =>
        {
            var counts = new Dictionary<PendingType, int>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT PendingType, COUNT(*) FROM SyncItems WHERE Status = @status AND PendingType IS NOT NULL GROUP BY PendingType";
            cmd.Parameters.AddWithValue("@status", (int)SyncStatus.Pending);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                counts[(PendingType)reader.GetInt32(0)] = reader.GetInt32(1);
            }

            return counts;
        },
        fallback: new Dictionary<PendingType, int>());

    /// <summary>
    /// Returns the byte-size breakdown for the items that affect storage:
    /// pending downloads/replacements + queued, minus pending deletions.
    /// </summary>
    public Dictionary<string, long> GetPendingSizes() => ExecuteRead(
        conn =>
        {
            var sizes = new Dictionary<string, long>
            {
                ["PendingDownload"] = SumSize(conn, SyncStatus.Pending, PendingType.Download),
                ["PendingReplacement"] = SumSize(conn, SyncStatus.Pending, PendingType.Replacement),
                ["PendingDeletion"] = SumSize(conn, SyncStatus.Pending, PendingType.Deletion),
                ["Queued"] = SumSize(conn, SyncStatus.Queued, pendingType: null)
            };
            sizes["Total"] = Math.Max(0, sizes["PendingDownload"] + sizes["PendingReplacement"] + sizes["Queued"] - sizes["PendingDeletion"]);
            return sizes;
        },
        fallback: new Dictionary<string, long>());

    /// <summary>
    /// Returns errored items that haven't exceeded the retry threshold,
    /// the candidates for the next sync run's retry pass.
    /// </summary>
    public IList<SyncItem> GetErroredItemsForRetry(int maxRetries) => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM SyncItems WHERE Status = @status AND (RetryCount IS NULL OR RetryCount < @maxRetries)";
            cmd.Parameters.AddWithValue("@status", (int)SyncStatus.Errored);
            cmd.Parameters.AddWithValue("@maxRetries", maxRetries);
            return ReadAll(cmd);
        },
        fallback: (IList<SyncItem>)Array.Empty<SyncItem>());

    /// <summary>
    /// Returns aggregate sync statistics for the dashboard.
    /// </summary>
    public SyncStats GetSyncStats() => ExecuteRead(
        conn =>
        {
            var stats = new SyncStats();

            using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = "SELECT Status, COUNT(*) FROM SyncItems GROUP BY Status";
                using var reader = countCmd.ExecuteReader();
                while (reader.Read())
                {
                    stats.StatusCounts[(SyncStatus)reader.GetInt32(0)] = reader.GetInt32(1);
                }
            }

            stats.TotalQueuedBytes = SumSize(conn, SyncStatus.Queued, pendingType: null);
            stats.TotalSyncedBytes = SumSize(conn, SyncStatus.Synced, pendingType: null);

            using (var lastSyncCmd = conn.CreateCommand())
            {
                lastSyncCmd.CommandText = "SELECT MAX(LastSyncTime) FROM SyncItems WHERE LastSyncTime IS NOT NULL";
                var lastSync = lastSyncCmd.ExecuteScalar();
                if (lastSync != null && lastSync != DBNull.Value)
                {
                    stats.LastSyncTime = ParseTimestamp((string)lastSync);
                }
            }

            return stats;
        },
        fallback: new SyncStats());

    /// <summary>
    /// Updates the status of a sync item by source-item ID, optionally
    /// rewriting Content-specific fields (PendingType, local-side file
    /// info, companion files). Uses
    /// <see cref="SyncTableManagerBase{TRecord, TKey}.BuildStatusTransitionClauses"/>
    /// with retry tracking, so retry semantics
    /// (zero on Synced, increment on Errored) match the rest of the
    /// universal transition logic.
    /// </summary>
    public void UpdateStatus(
        string sourceItemId,
        SyncStatus status,
        PendingType? pendingType = null,
        string? localItemId = null,
        string? localPath = null,
        string? errorMessage = null,
        long? sourceSize = null,
        string? companionFiles = null) => ExecuteWrite(conn =>
    {
        using var cmd = conn.CreateCommand();
        // Queued through this path is an operator action (Queue / Retry), and
        // the base transition clauses reset RetryCount for it.
        var clauses = BuildStatusTransitionClauses(status);

        // PendingType is set when transitioning to Pending, otherwise cleared.
        clauses.Add(status == SyncStatus.Pending ? "PendingType = @pendingType" : "PendingType = NULL");

        if (localItemId != null)
        {
            clauses.Add("LocalItemId = @localItemId");
        }

        if (localPath != null)
        {
            clauses.Add("LocalPath = @localPath");
        }

        if (sourceSize.HasValue)
        {
            clauses.Add("SourceSize = @sourceSize");
        }

        if (companionFiles != null)
        {
            clauses.Add("CompanionFiles = @companionFiles");
        }

        cmd.CommandText = $"UPDATE SyncItems SET {string.Join(", ", clauses)} WHERE SourceItemId = @key";
        AddStatusTransitionParameters(cmd, status, errorMessage);
        cmd.Parameters.AddWithValue("@key", sourceItemId);
        cmd.Parameters.AddWithValue("@pendingType", pendingType.HasValue ? (object)(int)pendingType.Value : DBNull.Value);

        if (localItemId != null)
        {
            cmd.Parameters.AddWithValue("@localItemId", localItemId);
        }

        if (localPath != null)
        {
            cmd.Parameters.AddWithValue("@localPath", localPath);
        }

        if (sourceSize.HasValue)
        {
            cmd.Parameters.AddWithValue("@sourceSize", sourceSize.Value);
        }

        if (companionFiles != null)
        {
            cmd.Parameters.AddWithValue("@companionFiles", companionFiles);
        }

        cmd.ExecuteNonQuery();
    });

    /// <summary>
    /// Updates status for many items by source-item ID in one transaction.
    /// Uses the same transition logic as <see cref="UpdateStatus"/> with
    /// retry tracking, Synced clears RetryCount, Errored increments it.
    /// Always clears PendingType (these batch transitions don't carry one).
    /// </summary>
    public int BatchUpdateStatus(IEnumerable<string> sourceItemIds, SyncStatus status, string? errorMessage = null)
    {
        ArgumentNullException.ThrowIfNull(sourceItemIds);
        var count = 0;
        ExecuteWrite(conn =>
        {
            using var transaction = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            var clauses = BuildStatusTransitionClauses(status);
            clauses.Add("PendingType = NULL");
            cmd.CommandText = $"UPDATE SyncItems SET {string.Join(", ", clauses)} WHERE SourceItemId = @sourceItemId";
            AddStatusTransitionParameters(cmd, status, errorMessage);
            var idParam = cmd.Parameters.Add("@sourceItemId", SqliteType.Text);
            foreach (var id in sourceItemIds)
            {
                idParam.Value = id;
                count += cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        });
        return count;
    }

    private static void BindSearchFilters(SqliteCommand cmd, string? searchTerm, SyncStatus? status, PendingType? pendingType, IReadOnlyList<string>? sourceItemIds)
    {
        for (var i = 0; i < (sourceItemIds?.Count ?? 0); i++)
        {
            cmd.Parameters.AddWithValue("@id" + i.ToString(CultureInfo.InvariantCulture), sourceItemIds![i]);
        }

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            cmd.Parameters.AddWithValue("@search", $"%{searchTerm}%");
        }

        if (status.HasValue)
        {
            cmd.Parameters.AddWithValue("@status", (int)status.Value);
        }

        if (pendingType.HasValue)
        {
            cmd.Parameters.AddWithValue("@pendingType", (int)pendingType.Value);
        }
    }

    private static long SumSize(SqliteConnection conn, SyncStatus status, PendingType? pendingType)
    {
        using var cmd = conn.CreateCommand();
        if (pendingType.HasValue)
        {
            cmd.CommandText = "SELECT COALESCE(SUM(SourceSize), 0) FROM SyncItems WHERE Status = @status AND PendingType = @pendingType";
            cmd.Parameters.AddWithValue("@pendingType", (int)pendingType.Value);
        }
        else
        {
            cmd.CommandText = "SELECT COALESCE(SUM(SourceSize), 0) FROM SyncItems WHERE Status = @status";
        }

        cmd.Parameters.AddWithValue("@status", (int)status);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
