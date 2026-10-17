using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using JPKribs.Jellyfin.Base;
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
        ImportLegacyFile();
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

    private void ImportLegacyFile()
    {
        if (Plugin.Instance?.DataFolderPath is not { Length: > 0 } folder)
        {
            return;
        }

        var path = Path.Combine(folder, "playlist-copies.json");
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, PlaylistCopy>>(File.ReadAllText(path)) ?? new Dictionary<string, PlaylistCopy>();
            foreach (var (id, copy) in stored)
            {
                if (copy is not null && Guid.TryParse(id, out var playlistId))
                {
                    Set(playlistId, copy.Root, copy.Server);
                }
            }

            File.Delete(path);
            Logger.LogInformation("Moved {Count} playlist copies from playlist-copies.json into the sync database", stored.Count);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Logger.LogWarning(ex, "Could not import playlist-copies.json");
        }
    }
}
