using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.Common;
using JPKribs.Jellyfin.Base;

namespace Jellyfin.Plugin.ServerSync.Services.Database;

/// <summary>
/// Reads and writes the rows of one sync table.
/// </summary>
/// <typeparam name="TRecord">The row type the sync table holds.</typeparam>
/// <typeparam name="TKey">The natural key that names one row.</typeparam>
public interface ISyncTableManager<TRecord, TKey>
    where TRecord : SyncRecord
    where TKey : notnull
{
    /// <summary>
    /// Returns the record with the given primary-key Id, or null if none.
    /// </summary>
    /// <param name="id">The primary-key id of the row to read.</param>
    /// <returns>The record, or null.</returns>
    TRecord? GetById(long id);

    /// <summary>
    /// Returns the record matching the given natural key, or null if none.
    /// </summary>
    /// <param name="key">The natural key of the row to read.</param>
    /// <returns>The record, or null.</returns>
    TRecord? GetByKey(TKey key);

    /// <summary>
    /// Returns all records with the given status, optionally limited.
    /// </summary>
    /// <param name="status">The status to filter by.</param>
    /// <param name="limit">The most records to return, or null for all.</param>
    /// <returns>The records.</returns>
    IList<TRecord> GetByStatus(SyncStatus status, int? limit = null);

    /// <summary>
    /// Returns all records in the table, and throws on a database error where the other reads return an empty list.
    /// A scan reads its existing rows with this, since an empty answer would make every item look new and overwrite Ignored and Pending rows.
    /// </summary>
    /// <returns>Every record.</returns>
    IList<TRecord> GetAllStrict();

    /// <summary>
    /// Returns all records with the given status, and throws on a database error where the other reads return an empty list.
    /// Use it where an empty answer would be read as nothing to do and record a clean run.
    /// </summary>
    /// <param name="status">The status to filter by.</param>
    /// <returns>All records with that status.</returns>
    IList<TRecord> GetByStatusStrict(SyncStatus status);

    /// <summary>
    /// Returns the total row count.
    /// </summary>
    /// <returns>The row count.</returns>
    int Count();

    /// <summary>
    /// Returns the row count for a given status.
    /// </summary>
    /// <param name="status">The status to count.</param>
    /// <returns>The row count.</returns>
    int CountByStatus(SyncStatus status);

    /// <summary>
    /// Inserts or updates a record by natural key.
    /// </summary>
    /// <param name="record">The record to write.</param>
    void Upsert(TRecord record);

    /// <summary>
    /// Inserts or updates many records, in one transaction where the table can. A record that fails is
    /// handed to <paramref name="onFailure"/> and the rest are still written.
    /// </summary>
    /// <param name="records">The records to write.</param>
    /// <param name="onFailure">Called with each record that could not be written.</param>
    /// <returns>How many were written.</returns>
    int UpsertMany(IReadOnlyList<TRecord> records, Action<TRecord, Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(onFailure);
        var written = 0;
        foreach (var record in records)
        {
            try
            {
                Upsert(record);
                written++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                onFailure(record, ex);
            }
        }

        return written;
    }

    /// <summary>
    /// Deletes a row by primary-key Id.
    /// </summary>
    /// <param name="id">The primary-key id of the row to delete.</param>
    void DeleteById(long id);

    /// <summary>
    /// Deletes many rows by primary-key Id, in one transaction where the table can.
    /// </summary>
    /// <param name="ids">The primary-key ids of the rows to delete.</param>
    /// <returns>How many rows were deleted.</returns>
    int DeleteByIds(IReadOnlyCollection<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        foreach (var id in ids)
        {
            DeleteById(id);
        }

        return ids.Count;
    }

    /// <summary>
    /// Deletes a row by natural key.
    /// </summary>
    /// <param name="key">The natural key of the row to delete.</param>
    void DeleteByKey(TKey key);

    /// <summary>
    /// Deletes every row in the table whatever its status, for a sync type's Reset.
    /// </summary>
    /// <returns>How many rows were deleted.</returns>
    int ResetTable();

    /// <summary>
    /// Updates only the status and reason of a row.
    /// </summary>
    /// <param name="id">The primary-key id of the row to update.</param>
    /// <param name="status">The new status.</param>
    /// <param name="reason">The reason, for a status that carries one.</param>
    void UpdateStatus(long id, SyncStatus status, string? reason = null);

    /// <summary>
    /// Updates only the status and reason of a row by natural key.
    /// </summary>
    /// <param name="key">The natural key of the row to update.</param>
    /// <param name="status">The new status.</param>
    /// <param name="reason">The reason, for a status that carries one.</param>
    void UpdateStatusByKey(TKey key, SyncStatus status, string? reason = null);

    /// <summary>
    /// Returns one page of records with optional filtering, search, and sort.
    /// </summary>
    /// <param name="request">The page, filter, search, and sort asked for.</param>
    /// <returns>The page.</returns>
    PagedResult<TRecord> Paginate(PaginationRequest request);
}
