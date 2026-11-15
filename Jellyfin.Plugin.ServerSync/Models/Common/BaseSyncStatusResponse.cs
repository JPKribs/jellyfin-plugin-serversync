using System;

namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>
/// The status counts every sync type's status response carries.
/// </summary>
public class BaseSyncStatusResponse
{
    /// <summary>
    /// Gets or sets the count of items pending review.
    /// </summary>
    public int Pending { get; set; }

    /// <summary>
    /// Gets or sets the count of items queued for sync.
    /// </summary>
    public int Queued { get; set; }

    /// <summary>
    /// Gets or sets the count of synced items.
    /// </summary>
    public int Synced { get; set; }

    /// <summary>
    /// Gets or sets the count of items with errors.
    /// </summary>
    public int Errored { get; set; }

    /// <summary>
    /// Gets or sets the count of ignored items.
    /// </summary>
    public int Ignored { get; set; }

    /// <summary>
    /// Gets the total count of all items.
    /// </summary>
    public virtual int Total => Pending + Queued + Synced + Errored + Ignored;

    /// <summary>
    /// Gets or sets the phase the last run failed in, "Refresh" or "Sync", or null when it ended cleanly.
    /// </summary>
    public string? LastFailurePhase { get; set; }

    /// <summary>
    /// Gets or sets why the last run failed, shown on the dashboard, or null when it ended cleanly.
    /// </summary>
    public string? LastFailureReason { get; set; }

    /// <summary>
    /// Gets or sets the UTC time the last run failed, or null when it ended cleanly.
    /// </summary>
    public DateTime? LastFailureTime { get; set; }
}
