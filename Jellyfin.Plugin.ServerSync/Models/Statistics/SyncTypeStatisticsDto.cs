using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Statistics;

/// <summary>
/// The last scans of one sync type that is turned on.
/// </summary>
public class SyncTypeStatisticsDto
{
    /// <summary>
    /// Gets or sets the sync type's key: Content, History, Metadata, People, or User.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the last scans, oldest first.
    /// </summary>
    public IReadOnlyList<ScanRunDto> Runs { get; set; } = new List<ScanRunDto>();
}
