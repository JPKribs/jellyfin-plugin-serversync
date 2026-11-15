using Jellyfin.Plugin.ServerSync.Services.Database;

namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>
/// Parameters for a paginated query against an <see cref="ISyncTableManager{TRecord, TKey}"/>.
/// </summary>
public sealed class PaginationRequest
{
    /// <summary>
    /// Gets the 1-based page number.
    /// </summary>
    public int Page { get; init; } = 1;

    /// <summary>
    /// Gets the page size, which a manager may clamp to its own upper bound.
    /// </summary>
    public int PageSize { get; init; } = 50;

    /// <summary>
    /// Gets an optional status filter. Null returns all statuses.
    /// </summary>
    public SyncStatus? StatusFilter { get; init; }

    /// <summary>
    /// Gets an optional search term. Each manager defines which columns it searches.
    /// </summary>
    public string? SearchTerm { get; init; }

    /// <summary>
    /// Gets the column to sort by. A manager falls back to its default for a column it does not know.
    /// </summary>
    public string? SortColumn { get; init; }

    /// <summary>
    /// Gets a value indicating whether the sort should be descending.
    /// </summary>
    public bool SortDescending { get; init; }
}
