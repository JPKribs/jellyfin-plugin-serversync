using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Marks the copies that were tracked before a copy's origin was recorded. Such a row says neither
/// where its file came from nor whether the file was this server's to begin with, so each is looked
/// at once: one that never went through a download was on this server already and is its own, and
/// one that did was given by the server its row names.
/// </summary>
public static class ExistingCopies
{
    /// <summary>Marks every settled copy that carries neither an origin nor a reason it is kept.</summary>
    /// <param name="manager">The content sync table.</param>
    /// <param name="config">The configuration, for each row's server.</param>
    /// <returns>How many rows were marked.</returns>
    public static int Mark(ContentSyncTableManager manager, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(config);
        var marked = 0;
        foreach (var row in manager.GetAllStrict())
        {
            if (row.StorageMode != StorageMode.Copy || row.Status != SyncStatus.Synced
                || row.Kept != KeptReason.None || !string.IsNullOrEmpty(row.OriginServerId)
                || string.IsNullOrEmpty(row.LocalPath) || !File.Exists(row.LocalPath))
            {
                continue;
            }

            // A row is stamped with the time of its sync only when a download finished. A file that
            // was matched where it stood never has one.
            if (row.LastSyncTime is null)
            {
                row.Kept = KeptReason.Own;
            }
            else if (config.FindServer(row.ServerKey) is { ServerId.Length: > 0 } server)
            {
                row.OriginServerId = server.ServerId;
            }
            else
            {
                continue;
            }

            manager.Upsert(row);
            marked++;
        }

        return marked;
    }
}
