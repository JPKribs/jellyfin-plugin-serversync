using System;

namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>
/// Base class for every record persisted in a sync table. Carries the universal
/// status-tracking fields. Each module defines its own subclass with the
/// payload-specific fields (typically a set of <see cref="SyncableValue{T}"/>
/// fields) and overrides <see cref="HasChanges"/>.
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
    /// Gets or sets the UTC timestamp of the last status change.
    /// </summary>
    public DateTime StatusDate { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp of the last successful sync, if any.
    /// </summary>
    public DateTime? LastSyncTime { get; set; }

    /// <summary>
    /// Gets or sets a human-readable explanation associated with the current
    /// status. Populated for <see cref="SyncStatus.Errored"/> (error message)
    /// and <see cref="SyncStatus.Ignored"/> (why-ignored). Null otherwise.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Gets or sets how many times an apply has failed for this row since the
    /// last success. Refresh refuses to re-queue a row that has reached
    /// <c>MaxRetryCount</c>, so a row that can never converge stops being
    /// re-applied on every run instead of churning forever. Reset to zero on a
    /// successful apply and by an explicit operator requeue.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Gets or sets the key of the configured server entry this row was built from. Null on rows written
    /// before servers became a list, which belong to the first scan server.
    /// </summary>
    public string? ServerKey { get; set; }

    /// <summary>
    /// Marks the row as settled without writing anything, because this server's value is kept. The
    /// stored hash is left alone, so the next scan compares again.
    /// </summary>
    /// <param name="reason">Why the value was kept, shown on the dashboard.</param>
    public void MarkKept(string reason)
    {
        Status = SyncStatus.Synced;
        StatusDate = DateTime.UtcNow;
        Reason = reason;
    }

    /// <summary>Marks the row as applied: settled now, with no reason or retries left over.</summary>
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
    /// Queues a row whose retries ran out once more. A fresh change is a fresh reason to try it, and an
    /// errored row would otherwise read as already settled.
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
    /// Gets a value indicating whether this record has differences that should
    /// be synced. Implementations typically OR together the
    /// <see cref="SyncableValue{T}.HasChanges"/> of their constituent fields.
    /// </summary>
    public abstract bool HasChanges { get; }

    /// <summary>
    /// Marks this record as successfully synced. Implementations call
    /// <see cref="SyncableValue{T}.MarkSynced"/> on each constituent field to
    /// record the applied baseline in <see cref="SyncableValue{T}.Synced"/> /
    /// <see cref="SyncableValue{T}.SyncedHash"/>. Change detection does not
    /// consult those, see <see cref="SyncableValue{T}.HasChanges"/>.
    /// </summary>
    public abstract void MarkSynced();
}
