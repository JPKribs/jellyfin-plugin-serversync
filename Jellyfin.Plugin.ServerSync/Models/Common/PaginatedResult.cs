using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>
/// Generic paginated result wrapper for API responses.
/// </summary>
/// <typeparam name="T">Type of items in the result.</typeparam>
public class PaginatedResult<T>
{
    /// <summary>
    /// Gets or sets the items for the current page.
    /// </summary>
    public List<T> Items { get; set; } = new();

    /// <summary>
    /// Gets or sets the total count of all items matching the filter.
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// Gets or sets the number of items skipped (offset).
    /// </summary>
    public int Skip { get; set; }

    /// <summary>
    /// Gets or sets the maximum items per page.
    /// </summary>
    public int Take { get; set; }

    /// <summary>
    /// Gets a value indicating whether there are more pages available.
    /// </summary>
    public bool HasMore => Skip + Items.Count < TotalCount;

    /// <summary>
    /// Gets the current page number (1-based).
    /// </summary>
    public int CurrentPage => Take > 0 ? (Skip / Take) + 1 : 1;

    /// <summary>
    /// Gets the total number of pages.
    /// </summary>
    public int TotalPages => Take > 0 ? (int)Math.Ceiling(TotalCount / (double)Take) : 1;
}
