using Jellyfin.Plugin.ServerSync.Models.Common;

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
    /// Gets the page size. Capped per-implementation; managers may clamp
    /// excessive values to a reasonable upper bound.
    /// </summary>
    public int PageSize { get; init; } = 50;

    /// <summary>
    /// Gets an optional status filter; null returns all statuses.
    /// </summary>
    public SyncStatus? StatusFilter { get; init; }

    /// <summary>
    /// Gets an optional free-text search term. Each manager defines which
    /// columns are searched (typically a primary name/path field).
    /// </summary>
    public string? SearchTerm { get; init; }

    /// <summary>
    /// Gets the column to sort by; managers define their own valid set and
    /// fall back to an internal default for unknown values.
    /// </summary>
    public string? SortColumn { get; init; }

    /// <summary>
    /// Gets a value indicating whether the sort should be descending.
    /// </summary>
    public bool SortDescending { get; init; }
}
