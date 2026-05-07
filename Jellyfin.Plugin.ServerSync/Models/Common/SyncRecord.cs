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
    /// Replaces the legacy <c>ErrorMessage</c> field used by the per-module
    /// records.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Gets a value indicating whether this record has differences that should
    /// be synced. Implementations typically OR together the
    /// <see cref="SyncableValue{T}.HasChanges"/> of their constituent fields.
    /// </summary>
    public abstract bool HasChanges { get; }

    /// <summary>
    /// Marks this record as having been successfully synced. Implementations
    /// should call <see cref="SyncableValue{T}.MarkSynced"/> on each of their
    /// constituent fields so future Refresh runs can short-circuit via
    /// <see cref="SyncableValue{T}.SyncedHash"/>. Called by
    /// <c>SyncQueueTaskBase</c> after a successful apply, and by
    /// <c>RefreshSyncTaskBase</c> when no changes are detected.
    /// </summary>
    public abstract void MarkSynced();
}
