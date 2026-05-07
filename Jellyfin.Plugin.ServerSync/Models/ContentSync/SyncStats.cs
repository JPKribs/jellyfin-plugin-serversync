using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.Common;

namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Statistics about the sync database.
/// </summary>
public class SyncStats
{
    public Dictionary<SyncStatus, int> StatusCounts { get; set; } = new();

    public long TotalQueuedBytes { get; set; }

    public long TotalSyncedBytes { get; set; }

    public DateTime? LastSyncTime { get; set; }
}
