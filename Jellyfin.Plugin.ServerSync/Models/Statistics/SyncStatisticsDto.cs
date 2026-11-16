using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Statistics;

/// <summary>
/// What the Statistics tab charts.
/// </summary>
public class SyncStatisticsDto
{
    /// <summary>
    /// Gets or sets the sync types that are turned on, each with its last scans. A sync type that is off is left out.
    /// </summary>
    public IReadOnlyList<SyncTypeStatisticsDto> Types { get; set; } = new List<SyncTypeStatisticsDto>();

    /// <summary>
    /// Gets or sets the link streams of the last thirty days for each server that may link to this one, or null when none may.
    /// </summary>
    public LinkStreamsDto? LinkStreams { get; set; }
}
