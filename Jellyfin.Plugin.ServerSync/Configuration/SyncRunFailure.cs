using System;

namespace Jellyfin.Plugin.ServerSync.Configuration;

/// <summary>
/// One run of a sync type that stopped before it finished.
/// </summary>
public sealed class SyncRunFailure
{
    /// <summary>
    /// Gets or sets when this failure was first recorded for its sync type and phase.
    /// A run that fails again keeps it, so it says how long the failing has lasted.
    /// </summary>
    public DateTime? FirstSeen { get; set; }

    /// <summary>Gets or sets a value indicating whether the administrator has been told about this failure.</summary>
    public bool Reported { get; set; }

    /// <summary>
    /// Gets or sets the sync type's key, "Content", "History", "Metadata", "People", or "User".
    /// </summary>
    public string ModuleKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the phase that failed, "Refresh" or "Sync".</summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>Gets or sets the reason in one line, shown on the dashboard.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets or sets the UTC time the failure was recorded.</summary>
    public DateTime Timestamp { get; set; }
}
