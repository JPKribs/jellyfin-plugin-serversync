#pragma warning disable CA2100 // SQL is internal and parameterized.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Stores the stamp each tracked object carries on this server.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class VersionStore : QueueStoreBase
{
    /// <summary>The most keys in one statement, which stays well under SQLite's parameter limit.</summary>
    private const int ChunkSize = 400;

    /// <summary>
    /// Initializes a new instance of the <see cref="VersionStore"/> class.
    /// </summary>
    /// <param name="databaseProvider">Opens the database the stamps are stored in.</param>
    /// <param name="logger">Writes database failures.</param>
    public VersionStore(ISyncDatabaseProvider databaseProvider, ILogger<VersionStore> logger)
        : base(databaseProvider, logger)
    {
    }

    /// <summary>Reads the stamp of one object.</summary>
    /// <param name="kind">The sync type of the object.</param>
    /// <param name="key">This server's key.</param>
    /// <returns>The stamp, or null when none is recorded.</returns>
    public ObjectVersion? Get(HintKind kind, string key) => Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ServerId, Timestamp FROM ObjectVersions WHERE Kind = @kind AND Key = @key";
        Add(cmd, "@kind", (int)kind);
        Add(cmd, "@key", key);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ObjectVersion { Kind = kind, Key = key, ServerId = reader.GetString(0), Timestamp = Unstamp(reader.GetString(1)) };
    });

    /// <summary>
    /// Reads the stamps of several objects in one statement per chunk, since a peer may ask for hundreds at once.
    /// </summary>
    /// <param name="kind">The sync type of the object.</param>
    /// <param name="keys">This server's keys for the objects.</param>
    /// <returns>The stamps found, without keys that have none.</returns>
    public IList<ObjectVersion> GetMany(HintKind kind, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var wanted = keys.Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.Ordinal).ToList();
        var found = new List<ObjectVersion>();
        for (var offset = 0; offset < wanted.Count; offset += ChunkSize)
        {
            var chunk = wanted.GetRange(offset, Math.Min(ChunkSize, wanted.Count - offset));
            found.AddRange(Read(conn =>
            {
                using var cmd = conn.CreateCommand();
                var names = new string[chunk.Count];
                for (var i = 0; i < chunk.Count; i++)
                {
                    names[i] = "@k" + i.ToString(CultureInfo.InvariantCulture);
                    Add(cmd, names[i], chunk[i]);
                }

                cmd.CommandText = "SELECT Key, ServerId, Timestamp FROM ObjectVersions WHERE Kind = @kind AND Key IN (" + string.Join(",", names) + ")";
                Add(cmd, "@kind", (int)kind);
                using var reader = cmd.ExecuteReader();
                var rows = new List<ObjectVersion>();
                while (reader.Read())
                {
                    rows.Add(new ObjectVersion { Kind = kind, Key = reader.GetString(0), ServerId = reader.GetString(1), Timestamp = Unstamp(reader.GetString(2)) });
                }

                return rows;
            }));
        }

        return found;
    }

    /// <summary>
    /// Records the stamp of one object when it is newer than the one held, by time and then by server id.
    /// A stamp never moves backwards, and a time ahead of this server's clock is stored as the current time.
    /// </summary>
    /// <param name="version">The stamp to record.</param>
    /// <returns><c>true</c> when it was stored, <c>false</c> when the one held is at least as new.</returns>
    public bool Set(ObjectVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var at = HintProtocol.BoundVersion(version.Timestamp, DateTime.UtcNow);
        return Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO ObjectVersions (Kind, Key, ServerId, Timestamp) VALUES (@kind, @key, @server, @at)
                ON CONFLICT(Kind, Key) DO UPDATE SET ServerId = @server, Timestamp = @at
                WHERE @at > Timestamp OR (@at = Timestamp AND @server > ServerId)";
            Add(cmd, "@kind", (int)version.Kind);
            Add(cmd, "@key", version.Key);
            Add(cmd, "@server", version.ServerId);
            Add(cmd, "@at", Stamp(at));
            return cmd.ExecuteNonQuery() > 0;
        });
    }

    /// <summary>
    /// Keeps the stamp a peer held for a value the scan read from it, until that value is applied here.
    /// The stamp recorded after the apply then belongs to the value written, not to whatever the peer holds by then.
    /// </summary>
    /// <param name="version">The peer's stamp, keyed by this server's key.</param>
    public void SetPending(ObjectVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var at = HintProtocol.BoundVersion(version.Timestamp, DateTime.UtcNow);
        Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO PendingVersions (Kind, Key, ServerId, Timestamp, ReadAt) VALUES (@kind, @key, @server, @at, @now)
                ON CONFLICT(Kind, Key) DO UPDATE SET ServerId = @server, Timestamp = @at, ReadAt = @now";
            Add(cmd, "@kind", (int)version.Kind);
            Add(cmd, "@key", version.Key);
            Add(cmd, "@server", version.ServerId);
            Add(cmd, "@at", Stamp(at));
            Add(cmd, "@now", Stamp(DateTime.UtcNow));
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Forgets a pending stamp, for a value the scan read with no stamp or kept here.</summary>
    /// <param name="kind">The sync type of the object.</param>
    /// <param name="key">This server's key.</param>
    public void ClearPending(HintKind kind, string key) => Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM PendingVersions WHERE Kind = @kind AND Key = @key";
        Add(cmd, "@kind", (int)kind);
        Add(cmd, "@key", key);
        cmd.ExecuteNonQuery();
    });

    /// <summary>
    /// Records the pending stamp of a value that was applied, and forgets it.
    /// </summary>
    /// <param name="kind">The sync type of the object.</param>
    /// <param name="key">This server's key.</param>
    /// <returns><c>true</c> when a stamp was pending, or <c>false</c> when the source never recorded an edit.</returns>
    public bool PromotePending(HintKind kind, string key)
    {
        var pending = Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ServerId, Timestamp FROM PendingVersions WHERE Kind = @kind AND Key = @key";
            Add(cmd, "@kind", (int)kind);
            Add(cmd, "@key", key);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? new ObjectVersion { Kind = kind, Key = key, ServerId = reader.GetString(0), Timestamp = Unstamp(reader.GetString(1)) } : null;
        });
        if (pending is null)
        {
            return false;
        }

        Set(pending);
        ClearPending(kind, key);
        return true;
    }

    /// <summary>Removes pending stamps read before the cutoff, whose values never applied.</summary>
    /// <param name="readBefore">Pending stamps read before this are removed.</param>
    /// <returns>How many were removed.</returns>
    public int PrunePending(DateTime readBefore) => Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM PendingVersions WHERE ReadAt < @before";
        Add(cmd, "@before", Stamp(readBefore));
        return cmd.ExecuteNonQuery();
    });

    /// <summary>Lists the keys held for one kind, for pruning stamps of objects that no longer exist.</summary>
    /// <param name="kind">The sync type of the object.</param>
    /// <returns>The keys.</returns>
    public IList<string> GetKeys(HintKind kind) => Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Key FROM ObjectVersions WHERE Kind = @kind";
        Add(cmd, "@kind", (int)kind);
        using var reader = cmd.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read())
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    });

    /// <summary>Removes the stamps of objects that no longer exist here.</summary>
    /// <param name="kind">The sync type of the object.</param>
    /// <param name="keys">This server's keys for the objects that are gone.</param>
    /// <returns>How many were removed.</returns>
    public int Remove(HintKind kind, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var list = keys.ToList();
        return Write(conn =>
        {
            var removed = 0;
            using var transaction = conn.BeginTransaction();
            foreach (var key in list)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM ObjectVersions WHERE Kind = @kind AND Key = @key";
                Add(cmd, "@kind", (int)kind);
                Add(cmd, "@key", key);
                removed += cmd.ExecuteNonQuery();
            }

            transaction.Commit();
            return removed;
        });
    }
}
