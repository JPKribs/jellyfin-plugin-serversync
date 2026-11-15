using System;

namespace Jellyfin.Plugin.ServerSync.Configuration;

/// <summary>
/// Failure record for a sync run that aborted before normal completion.
/// </summary>
public sealed class SyncRunFailure
{
    /// <summary>
    /// Gets or sets when this failure was first recorded for its module and phase. A run that fails
    /// again keeps it, so it says how long the module has been failing, not when it last did.
    /// </summary>
    public DateTime? FirstSeen { get; set; }

    /// <summary>Gets or sets a value indicating whether the administrator has been told about this failure.</summary>
    public bool Reported { get; set; }

    /// <summary>
    /// Module mutex key, "Content", "History", "Metadata", "People", or "User".
    /// </summary>
    public string ModuleKey { get; set; } = string.Empty;

    /// <summary>"Refresh" or "Sync".</summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>One-line human-readable reason. Surfaced in the UI.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the failure was recorded.</summary>
    public DateTime Timestamp { get; set; }
}
