using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

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
