using System;

namespace Jellyfin.Plugin.ServerSync.Models.Statistics;

/// <summary>
/// One scan of a sync type, as the Statistics tab charts it.
/// </summary>
public class ScanRunDto
{
    /// <summary>
    /// Gets or sets when the scan finished, in UTC.
    /// </summary>
    public DateTime FinishedAt { get; set; }

    /// <summary>
    /// Gets or sets how many items the scan tracked for the first time.
    /// </summary>
    public int Added { get; set; }

    /// <summary>
    /// Gets or sets how many items already in step the scan found changed.
    /// </summary>
    public int Updated { get; set; }

    /// <summary>
    /// Gets or sets how many items the scan stopped tracking.
    /// </summary>
    public int Removed { get; set; }
}
