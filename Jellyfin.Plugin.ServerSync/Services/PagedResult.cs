using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// One page of results from <see cref="ISyncTableManager{TRecord, TKey}.Paginate"/>.
/// </summary>
/// <typeparam name="TRecord">Record type.</typeparam>
public sealed class PagedResult<TRecord>
{
    /// <summary>
    /// Gets the records on this page.
    /// </summary>
    public required IReadOnlyList<TRecord> Items { get; init; }

    /// <summary>
    /// Gets the total number of records matching the request (across all pages).
    /// </summary>
    public required int TotalCount { get; init; }

    /// <summary>
    /// Gets the 1-based page number returned.
    /// </summary>
    public required int Page { get; init; }

    /// <summary>
    /// Gets the page size used for this request.
    /// </summary>
    public required int PageSize { get; init; }

    /// <summary>
    /// Gets the total number of pages, computed from <see cref="TotalCount"/>
    /// and <see cref="PageSize"/>.
    /// </summary>
    public int TotalPages => PageSize > 0 ? (TotalCount + PageSize - 1) / PageSize : 0;
}
