using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Represents a top-level item from a source server library for the filter item picker.
/// </summary>
public class SourceLibraryItemDto
{
    /// <summary>
    /// Gets or sets the item ID on the source server.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the production year.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets the overview/description.
    /// </summary>
    public string? Overview { get; set; }

    /// <summary>
    /// Gets or sets the path on the source server.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item type (e.g., "Series", "Movie").
    /// </summary>
    public string? Type { get; set; }
}

/// <summary>
/// Response for the source files browsing endpoint.
/// </summary>
public class SourceLibraryItemsResponse
{
    /// <summary>
    /// Gets or sets the list of items.
    /// </summary>
    public List<SourceLibraryItemDto> Items { get; set; } = new();

    /// <summary>
    /// Gets or sets the total number of items available (for pagination).
    /// </summary>
    public int TotalCount { get; set; }
}
