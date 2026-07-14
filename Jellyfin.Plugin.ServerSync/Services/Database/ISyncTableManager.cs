using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.Common;
using JPKribs.Jellyfin.Base;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Per-table CRUD surface for sync records. One implementation per record
/// type; <see cref="SyncTableManagerBase{TRecord, TKey}"/> provides the
/// universal operations and concrete subclasses fill in schema-specific
/// methods (key lookup, upsert SQL, search columns).
/// </summary>
/// <typeparam name="TRecord">Record type extending <see cref="SyncRecord"/>.</typeparam>
/// <typeparam name="TKey">Natural-key type for the table.</typeparam>
public interface ISyncTableManager<TRecord, TKey>
    where TRecord : SyncRecord
    where TKey : notnull
{
    /// <summary>
    /// Returns the record with the given primary-key Id, or null if none.
    /// </summary>
    TRecord? GetById(long id);

    /// <summary>
    /// Returns the record matching the given natural key, or null if none.
    /// </summary>
    TRecord? GetByKey(TKey key);

    /// <summary>
    /// Returns all records with the given status, optionally limited.
    /// </summary>
    IList<TRecord> GetByStatus(SyncStatus status, int? limit = null);

    /// <summary>
    /// Returns all records in the table, propagating database errors instead
    /// of degrading to an empty list. Refresh tasks use this for the
    /// existing-row snapshot: an empty snapshot makes every source item look
    /// new, and the subsequent upserts would overwrite user-set statuses
    /// (Ignored, pending approvals) with freshly computed ones.
    /// </summary>
    IList<TRecord> GetAllStrict();

    /// <summary>
    /// Returns all records with the given status, letting database errors
    /// propagate instead of degrading to an empty list. Use where an
    /// empty-on-error answer would be read as "nothing to do" and stamp a
    /// clean run.
    /// </summary>
    /// <param name="status">Status to filter by.</param>
    /// <returns>All records with that status.</returns>
    IList<TRecord> GetByStatusStrict(SyncStatus status);

    /// <summary>
    /// Returns the total row count.
    /// </summary>
    int Count();

    /// <summary>
    /// Returns the row count for a given status.
    /// </summary>
    int CountByStatus(SyncStatus status);

    /// <summary>
    /// Inserts or updates a record by natural key.
    /// </summary>
    void Upsert(TRecord record);

    /// <summary>
    /// Deletes a row by primary-key Id.
    /// </summary>
    void DeleteById(long id);

    /// <summary>
    /// Deletes a row by natural key.
    /// </summary>
    void DeleteByKey(TKey key);

    /// <summary>
    /// Deletes all rows with the given status. Returns count deleted.
    /// </summary>
    int DeleteByStatus(SyncStatus status);

    /// <summary>
    /// Truncates the table — deletes every row regardless of status. Used
    /// by the per-module "Reset Database" admin actions.
    /// </summary>
    int ResetTable();

    /// <summary>
    /// Updates only the status (and reason) of a row, leaving payload alone.
    /// </summary>
    void UpdateStatus(long id, SyncStatus status, string? reason = null);

    /// <summary>
    /// Updates only the status (and reason) of a row by natural key.
    /// </summary>
    void UpdateStatusByKey(TKey key, SyncStatus status, string? reason = null);

    /// <summary>
    /// Updates the status of many rows in one transaction. Returns count updated.
    /// </summary>
    int BulkUpdateStatus(IReadOnlyList<long> ids, SyncStatus status, string? reason = null);

    /// <summary>
    /// Returns one page of records with optional filtering, search, and sort.
    /// </summary>
    PagedResult<TRecord> Paginate(PaginationRequest request);
}
