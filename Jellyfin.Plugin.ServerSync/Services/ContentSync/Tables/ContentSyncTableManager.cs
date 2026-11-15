#pragma warning disable CA2100 // SQL is internal/parameterized.
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services.Database;
using JPKribs.Jellyfin.Base;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;

/// <summary>
/// Reads and writes the Content sync table, keyed by <see cref="SyncItem.SourceItemId"/>.
/// </summary>
[PluginService(ServiceLifetime.Transient)]
public sealed class ContentSyncTableManager : SyncTableManagerBase<SyncItem, string>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ContentSyncTableManager"/> class.
    /// </summary>
    /// <param name="databaseProvider">Gives the sync database.</param>
    /// <param name="logger">Writes log entries.</param>
    public ContentSyncTableManager(ISyncDatabaseProvider databaseProvider, ILogger<ContentSyncTableManager> logger)
        : base(databaseProvider, logger)
    {
    }

    /// <inheritdoc />
    protected override string TableName => "SyncItems";

    /// <inheritdoc />
    protected override string KeyColumn => "SourceItemId";

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
        item.OriginServerId = ReadNullableString(reader, "OriginServerId");
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
            .Set("OriginServerId", record.OriginServerId)
            .SyncColumns(record);
        ExecuteWrite(upsert.Execute);
    }

    /// <summary>
    /// Returns the row for a source item, or null when there is none.
    /// </summary>
    /// <param name="sourceItemId">The source item's id.</param>
    /// <returns>The row, or null.</returns>
    public SyncItem? GetBySourceItemId(string sourceItemId) => GetByKey(sourceItemId);

    /// <summary>
    /// Returns the row that records a local path, or null when there is none.
    /// </summary>
    /// <param name="localPath">The local path to look up.</param>
    /// <returns>The row, or null.</returns>
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
    /// The row of another source item that records the same local path, or null when none does.
    /// </summary>
    /// <param name="localPath">The local path to look up.</param>
    /// <param name="exceptSourceItemId">The source item whose own row is passed over.</param>
    /// <returns>The other row, or null.</returns>
    public SyncItem? GetOtherByLocalPath(string localPath, string exceptSourceItemId) => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM SyncItems WHERE LocalPath = @path AND SourceItemId != @except LIMIT 1";
            cmd.Parameters.AddWithValue("@path", localPath);
            cmd.Parameters.AddWithValue("@except", exceptSourceItemId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? MapFromReader(reader) : null;
        },
        fallback: null);

    /// <summary>
    /// Marks the database as readable only by builds that know links, so an older build sets it aside instead of downloading over a link.
    /// </summary>
    public void RequireLinkAwareReaders() => ExecuteWrite(conn =>
    {
        var floor = SyncSchema.MinReaderFor(conn);
        if ((DatabaseSchema.GetMinReaderVersion(conn) ?? 0) < floor)
        {
            DatabaseSchema.Stamp(conn, Math.Max(floor, DatabaseSchema.GetVersion(conn)), floor);
        }
    });

    /// <summary>
    /// Returns every link row with a local path, and throws when the table cannot be read, so the link cache never takes a failed read for no links.
    /// </summary>
    /// <returns>The link rows.</returns>
    public IList<SyncItem> GetLinksStrict() => ReadStrict(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM SyncItems WHERE StorageMode IN (1, 2) AND LocalPath IS NOT NULL";
        return ReadAll(cmd);
    });

    /// <summary>
    /// Returns every link row with a local path.
    /// </summary>
    /// <returns>The link rows, or an empty list when the table cannot be read.</returns>
    public IList<SyncItem> GetLinks() => ExecuteRead<IList<SyncItem>>(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM SyncItems WHERE StorageMode IN (1, 2) AND LocalPath IS NOT NULL";
            return ReadAll(cmd);
        },
        fallback: new List<SyncItem>());

    /// <summary>
    /// Returns every link row with a local path, or null when the table cannot be read, so a failed read is never taken for no links.
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
    /// Records that a copy from another server was removed, with its origin.
    /// </summary>
    /// <param name="path">Where the copy sat.</param>
    /// <param name="originServerId">The copy's origin.</param>
    public void NoteRemovedCopy(string? path, string? originServerId)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(originServerId))
        {
            return;
        }

        ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO RemovedItems (ItemId, RemovedAt, Path, OriginServerId) VALUES ('', @at, @path, @origin)";
            cmd.Parameters.AddWithValue("@at", UpsertBuilder.Time(DateTime.UtcNow));
            cmd.Parameters.AddWithValue("@path", path);
            cmd.Parameters.AddWithValue("@origin", originServerId);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Returns the copies that carry an origin, or null when the table cannot be read.
    /// </summary>
    /// <returns>The rows, or null.</returns>
    public IList<SyncItem>? TryGetCopiesWithOrigin() => ExecuteRead<IList<SyncItem>?>(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM SyncItems WHERE StorageMode = 0 AND LocalPath IS NOT NULL AND OriginServerId IS NOT NULL AND OriginServerId <> ''";
            return ReadAll(cmd);
        },
        fallback: null);

    /// <summary>
    /// Tells whether another row holds a local path. Of two rows that both want it, the one tracked first goes ahead so neither waits forever.
    /// </summary>
    /// <param name="localPath">The local path to check.</param>
    /// <param name="excludeSourceItemId">The source item asking, whose own row is passed over, or null.</param>
    /// <returns><c>true</c> when another row holds the path.</returns>
    public bool CheckPathCollision(string localPath, string? excludeSourceItemId = null) => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            if (excludeSourceItemId != null)
            {
                cmd.CommandText = @"SELECT COUNT(*) FROM SyncItems other WHERE other.LocalPath = @path AND other.SourceItemId != @excludeId
                    AND (other.Status = @synced OR other.Id < COALESCE((SELECT Id FROM SyncItems WHERE SourceItemId = @excludeId), 9223372036854775807))";
                cmd.Parameters.AddWithValue("@excludeId", excludeSourceItemId);
                cmd.Parameters.AddWithValue("@synced", (int)SyncStatus.Synced);
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
    /// Deletes the row for a source item.
    /// </summary>
    /// <param name="sourceItemId">The source item's id.</param>
    public void Delete(string sourceItemId) => DeleteByKey(sourceItemId);

    /// <summary>
    /// Returns one page of rows that match the Content sync table's filters.
    /// </summary>
    /// <param name="searchTerm">Text to find in the paths, or null.</param>
    /// <param name="status">Only rows with this status, or null.</param>
    /// <param name="pendingType">Only rows with this pending type, or null.</param>
    /// <param name="skip">Rows to skip.</param>
    /// <param name="take">Rows to return.</param>
    /// <param name="sourceItemIds">Only these source items, or null.</param>
    /// <param name="links"><c>true</c> for links only, <c>false</c> for copies only, or null for both.</param>
    /// <param name="serverKey">Only rows of this server entry, or null for every server.</param>
    /// <returns>The page and the total.</returns>
    public (IList<SyncItem> Items, int TotalCount) SearchPaginated(
        string? searchTerm,
        SyncStatus? status = null,
        PendingType? pendingType = null,
        int skip = 0,
        int take = 50,
        IReadOnlyList<string>? sourceItemIds = null,
        bool? links = null,
        string? serverKey = null) => ExecuteRead<(IList<SyncItem> Items, int TotalCount)>(
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

            // A row changing storage counts under the storage it is leaving.
            if (links is { } wantLinks)
            {
                conditions.Add(wantLinks ? "StorageMode IN (1, 2)" : "StorageMode IN (0, 3)");
            }

            if (serverKey is not null)
            {
                conditions.Add("ServerKey = @server");
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
                BindSearchFilters(countCmd, searchTerm, status, pendingType, sourceItemIds, serverKey);
                totalCount = Convert.ToInt32(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            using var dataCmd = conn.CreateCommand();
            dataCmd.CommandText = PageQuery("SyncItems", whereClause, $"{StatusPriorityOrderBy}, LocalPath ASC, Id ASC");
            BindSearchFilters(dataCmd, searchTerm, status, pendingType, sourceItemIds, serverKey);
            dataCmd.Parameters.AddWithValue("@take", take);
            dataCmd.Parameters.AddWithValue("@skip", skip);
            return (ReadAll(dataCmd), totalCount);
        },
        fallback: (Array.Empty<SyncItem>(), 0));

    /// <summary>
    /// Returns one page of the permanent copies one server's Link libraries hold, with those turning back into links.
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
                data.CommandText = PageQuery("SyncItems", where, "LocalPath ASC, Id ASC");
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
    /// <returns>The count for each pending type that has rows.</returns>
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
    /// Returns the bytes waiting to download, replace, and delete, and their net total.
    /// </summary>
    /// <returns>The bytes under PendingDownload, PendingReplacement, PendingDeletion, Queued, and Total.</returns>
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
    /// Returns the Errored rows that still have tries left.
    /// </summary>
    /// <param name="maxRetries">The tries a row gets.</param>
    /// <returns>The rows to retry.</returns>
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
    /// Returns the rows that errored, used up their tries, and have sat that way since before a given time. A link the link check flagged is left out, since the check reads it on every run.
    /// </summary>
    /// <param name="maxRetries">The tries a row gets.</param>
    /// <param name="erroredBefore">Only rows errored before this.</param>
    /// <returns>The rows.</returns>
    public IList<SyncItem> GetErroredCopiesSince(int maxRetries, DateTime erroredBefore) => ExecuteRead(
        conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM SyncItems WHERE Status = @status AND RetryCount >= @maxRetries AND RetryCount < 1000000 AND StatusDate < @before";
            cmd.Parameters.AddWithValue("@status", (int)SyncStatus.Errored);
            cmd.Parameters.AddWithValue("@maxRetries", maxRetries);
            cmd.Parameters.AddWithValue("@before", UpsertBuilder.Time(erroredBefore));
            return ReadAll(cmd);
        },
        fallback: (IList<SyncItem>)Array.Empty<SyncItem>());

    /// <summary>
    /// Sets a row's status and any local details given with it. Synced and Queued clear the retry count, and Errored raises it.
    /// </summary>
    /// <param name="sourceItemId">The source item's id.</param>
    /// <param name="status">The new status.</param>
    /// <param name="pendingType">The pending type, kept only when the status is Pending.</param>
    /// <param name="localItemId">The local item id to record, or null to keep it.</param>
    /// <param name="localPath">The local path to record, or null to keep it.</param>
    /// <param name="errorMessage">The reason, for an Errored row.</param>
    /// <param name="sourceSize">The source size to record, or null to keep it.</param>
    /// <param name="companionFiles">The companion files to record, or null to keep them.</param>
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
        var clauses = BuildStatusTransitionClauses(status);

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
    /// Sets the status of many rows in one transaction, as <see cref="UpdateStatus"/> does, and clears each pending type.
    /// </summary>
    /// <param name="sourceItemIds">The source items to update.</param>
    /// <param name="status">The new status.</param>
    /// <param name="errorMessage">The reason, for Errored rows.</param>
    /// <returns>How many rows changed.</returns>
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

    private static void BindSearchFilters(SqliteCommand cmd, string? searchTerm, SyncStatus? status, PendingType? pendingType, IReadOnlyList<string>? sourceItemIds, string? serverKey)
    {
        if (serverKey is not null)
        {
            cmd.Parameters.AddWithValue("@server", serverKey);
        }

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
