namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>
/// Sync status values shared across all sync modules. Stored as INTEGER in SQLite.
/// </summary>
public enum SyncStatus
{
    /// <summary>
    /// Refresh has stored a snapshot but Compare has not yet decided the status.
    /// Transient state when Compare runs immediately after Refresh; visible if
    /// the two phases are scheduled separately.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Compare detected a meaningful difference; awaits Sync to apply it.
    /// </summary>
    Queued = 1,

    /// <summary>
    /// Sync applied the source value successfully and the synced fingerprint
    /// is recorded.
    /// </summary>
    Synced = 2,

    /// <summary>
    /// Sync failed; <c>Reason</c> on the record holds the error message.
    /// </summary>
    Errored = 3,

    /// <summary>
    /// User flagged this row to skip; <c>Reason</c> on the record holds the
    /// human-readable explanation.
    /// </summary>
    Ignored = 4,

    /// <summary>
    /// Content-sync only: row is queued for local file deletion. Treated
    /// like <see cref="Queued"/> in flows that don't distinguish operation
    /// type; the per-row <c>PendingType</c> column carries the discriminator
    /// when needed.
    /// </summary>
    Deleting = 5
}
