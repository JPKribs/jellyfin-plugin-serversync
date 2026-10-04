#pragma warning disable CA2100 // SQL is internal and parameterized.
using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// The hints peers have sent this server that are not yet applied. See <see cref="InboundHint"/>.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class InboundHintStore : QueueStoreBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InboundHintStore"/> class.
    /// </summary>
    /// <param name="databaseProvider">The database provider.</param>
    /// <param name="logger">Logger.</param>
    public InboundHintStore(ISyncDatabaseProvider databaseProvider, ILogger<InboundHintStore> logger)
        : base(databaseProvider, logger)
    {
    }

    /// <summary>
    /// Stores a received hint. A row for the same origin, kind, and key is refreshed with the newer
    /// hint id and version and made due at once, so a notice that arrives while an older one waits
    /// is never applied twice.
    /// </summary>
    /// <param name="row">The row.</param>
    public void Enqueue(InboundHint row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO InboundHints (
                    HintId, OriginServerId, Kind, Key, ItemPath, ItemId, UserId, UserName,
                    VersionServerId, VersionTimestamp, ReceivedAt, Attempts, NextAttempt, LastError
                ) VALUES (
                    @hint, @origin, @kind, @key, @itemPath, @itemId, @userId, @userName,
                    @versionServer, @versionAt, @received, 0, @received, NULL
                )
                ON CONFLICT(OriginServerId, Kind, Key) DO UPDATE SET
                    HintId = @hint,
                    ItemPath = @itemPath,
                    ItemId = @itemId,
                    UserId = @userId,
                    UserName = @userName,
                    VersionServerId = CASE WHEN @versionAt >= VersionTimestamp THEN @versionServer ELSE VersionServerId END,
                    VersionTimestamp = CASE WHEN @versionAt >= VersionTimestamp THEN @versionAt ELSE VersionTimestamp END,
                    ReceivedAt = @received,
                    Attempts = 0,
                    NextAttempt = @received,
                    LastError = NULL
                RETURNING Id";
            Add(cmd, "@hint", row.HintId);
            Add(cmd, "@origin", row.OriginServerId);
            Add(cmd, "@kind", (int)row.Kind);
            Add(cmd, "@key", row.Key);
            Add(cmd, "@itemPath", row.ItemPath);
            Add(cmd, "@itemId", row.ItemId);
            Add(cmd, "@userId", row.UserId);
            Add(cmd, "@userName", row.UserName);
            Add(cmd, "@versionServer", row.VersionServerId);
            Add(cmd, "@versionAt", Stamp(row.VersionTimestamp));
            Add(cmd, "@received", Stamp(row.ReceivedAt));
            row.Id = Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        });
    }

    /// <summary>Returns the due rows of one lane: content, whose applies download files, or everything else.</summary>
    /// <param name="utcNow">Now.</param>
    /// <param name="limit">The most rows to return.</param>
    /// <param name="content"><c>true</c> for content rows, <c>false</c> for the rest.</param>
    /// <returns>The rows.</returns>
    public IList<InboundHint> GetDue(DateTime utcNow, int limit, bool content) => Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = content
            ? "SELECT * FROM InboundHints WHERE NextAttempt <= @now AND Kind = @kind ORDER BY Id LIMIT @limit"
            : "SELECT * FROM InboundHints WHERE NextAttempt <= @now AND Kind <> @kind ORDER BY Id LIMIT @limit";
        Add(cmd, "@now", Stamp(utcNow));
        Add(cmd, "@kind", (int)HintKind.Content);
        Add(cmd, "@limit", limit);
        return ReadAll(cmd);
    });

    /// <summary>Returns every row, oldest first.</summary>
    /// <returns>The rows.</returns>
    public IList<InboundHint> GetAll() => GetOldest(int.MaxValue);

    /// <summary>Returns the oldest rows, for a view that polls often and must not carry the whole table.</summary>
    /// <param name="limit">The most rows to return.</param>
    /// <returns>The rows, oldest first.</returns>
    public IList<InboundHint> GetOldest(int limit) => Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM InboundHints ORDER BY Id LIMIT @limit";
        Add(cmd, "@limit", limit);
        return ReadAll(cmd);
    });

    /// <summary>Returns one row by id.</summary>
    /// <param name="id">The row id.</param>
    /// <returns>The row, or null.</returns>
    public InboundHint? Get(long id) => Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM InboundHints WHERE Id = @id";
        Add(cmd, "@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    });

    /// <summary>
    /// Removes a row once its work is done, but only while it still carries the version the apply worked
    /// from. The origin reuses one hint id per object, so the id cannot tell a refresh apart; the version
    /// can. A row refreshed with a newer version during the apply stays and is applied again.
    /// </summary>
    /// <param name="id">The row id.</param>
    /// <param name="appliedVersion">The version the apply worked from.</param>
    /// <returns><c>true</c> when the row was removed.</returns>
    public bool Remove(long id, DateTime appliedVersion) => Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM InboundHints WHERE Id = @id AND VersionTimestamp <= @version";
        Add(cmd, "@id", id);
        Add(cmd, "@version", Stamp(appliedVersion));
        return cmd.ExecuteNonQuery() > 0;
    });

    /// <summary>Removes a row by id regardless of its hint, for the operator.</summary>
    /// <param name="id">The row id.</param>
    /// <returns><c>true</c> when a row was removed.</returns>
    public bool Delete(long id) => Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM InboundHints WHERE Id = @id";
        Add(cmd, "@id", id);
        return cmd.ExecuteNonQuery() > 0;
    });

    /// <summary>Schedules a row for another attempt after a failed apply.</summary>
    /// <param name="id">The row id.</param>
    /// <param name="nextAttempt">When to try again.</param>
    /// <param name="error">What went wrong.</param>
    public void Defer(long id, DateTime nextAttempt, string error) => Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE InboundHints SET Attempts = Attempts + 1, NextAttempt = @next, LastError = @error WHERE Id = @id";
        Add(cmd, "@next", Stamp(nextAttempt));
        Add(cmd, "@error", error);
        Add(cmd, "@id", id);
        cmd.ExecuteNonQuery();
    });

    /// <summary>Counts the rows.</summary>
    /// <returns>The count.</returns>
    public int Count() => Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM InboundHints";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    });

    private static List<InboundHint> ReadAll(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var rows = new List<InboundHint>();
        while (reader.Read())
        {
            rows.Add(Map(reader));
        }

        return rows;
    }

    private static InboundHint Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("Id")),
        HintId = reader.GetString(reader.GetOrdinal("HintId")),
        OriginServerId = reader.GetString(reader.GetOrdinal("OriginServerId")),
        Kind = (HintKind)reader.GetInt32(reader.GetOrdinal("Kind")),
        Key = reader.GetString(reader.GetOrdinal("Key")),
        ItemPath = Text(reader, "ItemPath"),
        ItemId = Text(reader, "ItemId"),
        UserId = Text(reader, "UserId"),
        UserName = Text(reader, "UserName"),
        VersionServerId = reader.GetString(reader.GetOrdinal("VersionServerId")),
        VersionTimestamp = Unstamp(reader.GetString(reader.GetOrdinal("VersionTimestamp"))),
        ReceivedAt = Unstamp(reader.GetString(reader.GetOrdinal("ReceivedAt"))),
        Attempts = reader.GetInt32(reader.GetOrdinal("Attempts")),
        NextAttempt = Unstamp(reader.GetString(reader.GetOrdinal("NextAttempt"))),
        LastError = Text(reader, "LastError")
    };
}
