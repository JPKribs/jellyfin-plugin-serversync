using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.UserSync;

/// <summary>
/// Request for bulk user sync item operations.
/// </summary>
public class BulkUserSyncItemsRequest
{
    /// <summary>
    /// Gets or sets the list of database IDs.
    /// </summary>
    public List<long> Ids { get; set; } = new();

    /// <summary>
    /// Gets or sets a status to select rows by when <see cref="Ids"/> is empty. The Queue endpoint
    /// then queues every row in this status, which is how "Retry errors" reaches every errored row.
    /// </summary>
    public string? Status { get; set; }
}
