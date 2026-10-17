using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.ServerSync.Services.Playlists;

public static class PlaylistCopyRegistry
{
    private static readonly object Gate = new();
    private static Dictionary<string, PlaylistCopy>? _copies;

    public static PlaylistCopy? Get(Guid playlistId)
    {
        lock (Gate)
        {
            return Load().TryGetValue(Key(playlistId), out var copy) ? copy : null;
        }
    }

    public static void Set(Guid playlistId, string root, string serverKey)
    {
        lock (Gate)
        {
            Load()[Key(playlistId)] = new PlaylistCopy(root, serverKey);
            Save();
        }
    }

    public static void Remove(Guid playlistId)
    {
        lock (Gate)
        {
            if (Load().Remove(Key(playlistId)))
            {
                Save();
            }
        }
    }

    private static string Key(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    private static string? FilePath
        => Plugin.Instance?.DataFolderPath is { Length: > 0 } folder ? Path.Combine(folder, "playlist-copies.json") : null;

    private static Dictionary<string, PlaylistCopy> Load()
    {
        if (_copies is not null)
        {
            return _copies;
        }

        _copies = new Dictionary<string, PlaylistCopy>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (FilePath is { } path && File.Exists(path))
            {
                var stored = JsonSerializer.Deserialize<Dictionary<string, PlaylistCopy>>(File.ReadAllText(path));
                if (stored is not null)
                {
                    foreach (var (id, copy) in stored)
                    {
                        _copies[id] = copy;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return _copies;
    }

    private static void Save()
    {
        if (FilePath is not { } path || _copies is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_copies));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed record PlaylistCopy(string Root, string Server);
