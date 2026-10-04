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
    /// Records the version of one object, replacing any previous one. A timestamp ahead of this server's
    /// clock is stored as now, so no stored version can outrank every later edit made here.
    /// </summary>
    /// <param name="version">The version.</param>
    public void Set(ObjectVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var at = HintProtocol.BoundVersion(version.Timestamp, DateTime.UtcNow);
        Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO ObjectVersions (Kind, Key, ServerId, Timestamp) VALUES (@kind, @key, @server, @at)
                ON CONFLICT(Kind, Key) DO UPDATE SET ServerId = @server, Timestamp = @at";
            Add(cmd, "@kind", (int)version.Kind);
            Add(cmd, "@key", version.Key);
            Add(cmd, "@server", version.ServerId);
            Add(cmd, "@at", Stamp(at));
            cmd.ExecuteNonQuery();
        });
    }
}
