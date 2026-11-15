using System;

namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>
/// The base of every row in a sync table, carrying the status fields all sync types share.
/// </summary>
public abstract class SyncRecord
{
    /// <summary>
    /// Gets or sets the auto-increment primary key.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the current status of this row.
    /// </summary>
    public SyncStatus Status { get; set; }

    /// <summary>
    /// Gets or sets the UTC time of the last status change.
    /// </summary>
    public DateTime StatusDate { get; set; }

    /// <summary>
    /// Gets or sets the UTC time of the last successful sync, if any.
    /// </summary>
    public DateTime? LastSyncTime { get; set; }

    /// <summary>
    /// Gets or sets the error of an <see cref="SyncStatus.Errored"/> row or why an <see cref="SyncStatus.Ignored"/> row is ignored, and null otherwise.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Gets or sets how many times an apply has failed for this row since the last success.
    /// A scan does not queue a row that reached <c>MaxRetryCount</c>, so a row that can never settle stops being applied on every run.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Gets or sets the key of the server entry this row was built from. A row with none belongs to the first source.
    /// </summary>
    public string? ServerKey { get; set; }

    /// <summary>
    /// Marks the row as settled without writing anything, because this server's value is kept.
    /// The stored hash is left alone, so the next scan compares again.
    /// </summary>
    /// <param name="reason">Why the value was kept, shown on the dashboard.</param>
    public void MarkKept(string reason)
    {
        Status = SyncStatus.Synced;
        StatusDate = DateTime.UtcNow;
        Reason = reason;
    }

    /// <summary>Marks the row as applied, with no reason or retries left over.</summary>
    /// <param name="utcNow">The time of the apply.</param>
    public void MarkApplied(DateTime utcNow)
    {
        Status = SyncStatus.Synced;
        StatusDate = utcNow;
        LastSyncTime = utcNow;
        Reason = null;
        RetryCount = 0;
        MarkSynced();
    }

    /// <summary>
    /// Queues an errored row again, since a fresh change is a fresh reason to try it.
    /// </summary>
    public void RetryIfErrored()
    {
        if (Status == SyncStatus.Errored)
        {
            Status = SyncStatus.Queued;
            RetryCount = 0;
        }
    }

    /// <summary>
    /// Gets a value indicating whether this row has differences to sync.
    /// </summary>
    public abstract bool HasChanges { get; }

    /// <summary>
    /// Records what was applied on each of the row's values. Change detection does not read what this records.
    /// </summary>
    public abstract void MarkSynced();
}
