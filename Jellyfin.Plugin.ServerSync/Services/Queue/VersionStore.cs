#pragma warning disable CA2100 // SQL is internal and parameterized.
using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// The version each tracked object carries on this server. See <see cref="ObjectVersion"/>.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class VersionStore : QueueStoreBase
{
    // Well under SQLite's parameter limit, with room for the kind.
    private const int ChunkSize = 400;

    /// <summary>
    /// Initializes a new instance of the <see cref="VersionStore"/> class.
    /// </summary>
    /// <param name="databaseProvider">The database provider.</param>
    /// <param name="logger">Logger.</param>
    public VersionStore(ISyncDatabaseProvider databaseProvider, ILogger<VersionStore> logger)
        : base(databaseProvider, logger)
    {
    }

    /// <summary>Reads the version of one object, or null when none is recorded.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="key">This server's key.</param>
    /// <returns>The version, or null.</returns>
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
    /// Reads the versions of several objects in one statement per chunk, rather than one per key, since
    /// a peer may ask for hundreds at once. Keys with no version are left out.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <param name="keys">This server's keys.</param>
    /// <returns>The versions found.</returns>
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
                    names[i] = "@k" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
    /// Records the version of one object when it is newer than the one held, by time and then by server
    /// id, the same order every decision uses. A version never moves backwards, so a retried older hint
    /// or a late copy cannot undo a newer edit. A timestamp ahead of this server's clock is stored as now,
    /// so no stored version can outrank every later edit made here.
    /// </summary>
    /// <param name="version">The version.</param>
    /// <returns>True when it was stored, false when the one held is at least as new.</returns>
    public bool Set(ObjectVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var at = HintProtocol.BoundVersion(version.Timestamp, DateTime.UtcNow);
        var stored = 0;
        Write(conn =>
        {
            // Stamps are fixed width UTC text, so text order is time order.
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO ObjectVersions (Kind, Key, ServerId, Timestamp) VALUES (@kind, @key, @server, @at)
                ON CONFLICT(Kind, Key) DO UPDATE SET ServerId = @server, Timestamp = @at
                WHERE @at > Timestamp OR (@at = Timestamp AND @server > ServerId)";
            Add(cmd, "@kind", (int)version.Kind);
            Add(cmd, "@key", version.Key);
            Add(cmd, "@server", version.ServerId);
            Add(cmd, "@at", Stamp(at));
            stored = cmd.ExecuteNonQuery();
        });
        return stored > 0;
    }

    /// <summary>
    /// Keeps the version a peer held for a value the scan read from it, until that value is applied
    /// here. The scan reads values and versions together, so the version recorded after the apply is
    /// the one that belongs to the value written, not whatever the peer holds by then.
    /// </summary>
    /// <param name="version">The peer's version, keyed by this server's key.</param>
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

    /// <summary>Forgets a pending version, for a value the scan read with no version or kept here.</summary>
    /// <param name="kind">The kind.</param>
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
    /// Records the pending version of a value that was just applied, and forgets it. Nothing happens
    /// when no version was pending, since the value then came from a source that never recorded an edit.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <param name="key">This server's key.</param>
    /// <returns>True when a pending version existed.</returns>
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

    /// <summary>Removes pending versions read longer ago than the cutoff, whose values never applied.</summary>
    /// <param name="readBefore">The cutoff.</param>
    /// <returns>How many were removed.</returns>
    public int PrunePending(DateTime readBefore)
    {
        var removed = 0;
        Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM PendingVersions WHERE ReadAt < @before";
            Add(cmd, "@before", Stamp(readBefore));
            removed = cmd.ExecuteNonQuery();
        });
        return removed;
    }

    /// <summary>Lists the keys held for one kind, for pruning versions of objects that no longer exist.</summary>
    /// <param name="kind">The kind.</param>
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

        return (IList<string>)keys;
    });

    /// <summary>Removes the versions of objects that no longer exist here.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="keys">The keys to remove.</param>
    /// <returns>How many were removed.</returns>
    public int Remove(HintKind kind, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var removed = 0;
        var list = keys.ToList();
        Write(conn =>
        {
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
        });
        return removed;
    }
}
