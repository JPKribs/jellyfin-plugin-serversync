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

    /// <summary>
    /// Gets or sets a status to select rows by when <see cref="Ids"/> is empty. The Queue endpoint
    /// then queues every row in this status, which is how "Retry errors" reaches every errored row.
    /// </summary>
    public string? Status { get; set; }
}
