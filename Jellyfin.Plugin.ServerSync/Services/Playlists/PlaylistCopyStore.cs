using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Playlists;

public sealed record PlaylistCopy(string Root, string Server);

[PluginService(ServiceLifetime.Singleton)]
public sealed class PlaylistCopyStore : QueueStoreBase
{
    private static readonly ConcurrentDictionary<string, PlaylistCopy> Detached = new(StringComparer.OrdinalIgnoreCase);
    private static PlaylistCopyStore? _current;

    public PlaylistCopyStore(ISyncDatabaseProvider databaseProvider, ILogger<PlaylistCopyStore> logger)
        : base(databaseProvider, logger)
    {
        _current = this;
    }

    public static PlaylistCopy? Get(Guid playlistId)
    {
        var key = Key(playlistId);
        if (_current is null)
        {
            return Detached.TryGetValue(key, out var copy) ? copy : null;
        }

        return _current.Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT RootId, ServerKey FROM PlaylistCopies WHERE PlaylistId = @id";
            Add(cmd, "@id", key);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? new PlaylistCopy(reader.GetString(0), reader.GetString(1)) : null;
        });
    }

    public static void Set(Guid playlistId, string root, string serverKey)
    {
        var key = Key(playlistId);
        if (_current is null)
        {
            Detached[key] = new PlaylistCopy(root, serverKey);
            return;
        }

        _current.Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO PlaylistCopies (PlaylistId, RootId, ServerKey, CreatedAt) VALUES (@id, @root, @server, @at)
                ON CONFLICT(PlaylistId) DO UPDATE SET RootId = @root, ServerKey = @server";
            Add(cmd, "@id", key);
            Add(cmd, "@root", root);
            Add(cmd, "@server", serverKey);
            Add(cmd, "@at", Stamp(DateTime.UtcNow));
            cmd.ExecuteNonQuery();
        });
    }

    public static void Remove(Guid playlistId)
    {
        var key = Key(playlistId);
        if (_current is null)
        {
            Detached.TryRemove(key, out _);
            return;
        }

        _current.Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM PlaylistCopies WHERE PlaylistId = @id";
            Add(cmd, "@id", key);
            cmd.ExecuteNonQuery();
        });
    }

    public static int Count()
        => _current?.Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM PlaylistCopies";
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }) ?? Detached.Count;

    public static List<Guid> Ids()
        => _current?.Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT PlaylistId FROM PlaylistCopies";
            using var reader = cmd.ExecuteReader();
            var ids = new List<Guid>();
            while (reader.Read())
            {
                if (Guid.TryParse(reader.GetString(0), out var id))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }) ?? new List<Guid>();

    private static string Key(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// Forgets which playlists here are copies of a peer's. Each copy also carries that on itself, so
    /// the next playlist sync finds every copy again and none is made twice.
    /// </summary>
    /// <returns>How many copies were forgotten.</returns>
    public int Clear() => Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM PlaylistCopies";
        return cmd.ExecuteNonQuery();
    });
}
