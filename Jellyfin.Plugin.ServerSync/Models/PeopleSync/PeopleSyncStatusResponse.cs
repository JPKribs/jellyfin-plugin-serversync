using System;
using Jellyfin.Plugin.ServerSync.Models.Common;

namespace Jellyfin.Plugin.ServerSync.Models.PeopleSync;

/// <summary>
/// Response model for people sync status.
/// </summary>
public class PeopleSyncStatusResponse : BaseSyncStatusResponse
{
    /// <summary>
    /// Gets or sets the last sync time.
    /// </summary>
    public DateTime? LastSyncTime { get; set; }

    /// <summary>
    /// Gets or sets the total number of people tracked.
    /// </summary>
    public int PersonCount { get; set; }
}
