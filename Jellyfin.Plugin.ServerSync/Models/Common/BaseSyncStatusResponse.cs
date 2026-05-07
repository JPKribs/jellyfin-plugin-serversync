using System;

namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>
/// Base class for sync status responses with common status counts.
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
    /// Phase of the most-recent run failure ("Refresh" or "Sync"). Null
    /// when the last run completed cleanly.
    /// </summary>
    public string? LastFailurePhase { get; set; }

    /// <summary>
    /// Human-readable reason for the most-recent run failure. Null when the
    /// last run completed cleanly. The dashboard surfaces this so silent
    /// aborts (connection failure, disk space, circuit breaker) become
    /// visible without log-diving.
    /// </summary>
    public string? LastFailureReason { get; set; }

    /// <summary>
    /// UTC timestamp of the most-recent run failure. Null when the last run
    /// completed cleanly.
    /// </summary>
    public DateTime? LastFailureTime { get; set; }
}
