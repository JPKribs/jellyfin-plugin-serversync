namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>
/// The status of a row in any sync table, stored as an integer in SQLite.
/// </summary>
public enum SyncStatus
{
    /// <summary>
    /// The scan stored the row and has not yet decided its status, or the row waits for approval.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// The row differs from the source and waits for the next run to apply it.
    /// </summary>
    Queued = 1,

    /// <summary>
    /// The source's value was applied and its fingerprint recorded.
    /// </summary>
    Synced = 2,

    /// <summary>
    /// The apply failed. <c>Reason</c> on the row holds the error.
    /// </summary>
    Errored = 3,

    /// <summary>
    /// A user set the row to be skipped. <c>Reason</c> on the row says why.
    /// </summary>
    Ignored = 4,

    /// <summary>
    /// The row's local file is queued for deletion, which only Content uses.
    /// Flows that do not tell operations apart treat it like <see cref="Queued"/>.
    /// </summary>
    Deleting = 5
}
