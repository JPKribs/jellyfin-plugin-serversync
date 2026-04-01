using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.PeopleSync;

/// <summary>
/// Request model for bulk people sync item operations.
/// </summary>
public class BulkPeopleSyncItemsRequest
{
    /// <summary>
    /// Gets or sets the list of item IDs to operate on.
    /// </summary>
    public List<long> Ids { get; set; } = new();
}
