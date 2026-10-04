using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.HistorySync;

/// <summary>
/// Request for bulk operations on history sync items.
/// </summary>
public class BulkHistoryItemsRequest
{
    /// <summary>
    /// Gets or sets the list of database IDs to operate on.
    /// Preferred way to specify items.
    /// </summary>
    public List<long> Ids { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of items to operate on (legacy).
    /// Use Ids instead when possible.
    /// </summary>
    public List<HistoryItemReference> Items { get; set; } = new();

    /// <summary>
    /// Gets or sets a status to select rows by when <see cref="Ids"/> is empty. The Queue endpoint
    /// then queues every row in this status, which is how "Retry errors" reaches every errored row.
    /// </summary>
    public string? Status { get; set; }
}

/// <summary>
/// Reference to a history sync item by user and item ID.
/// </summary>
public class HistoryItemReference
{
    /// <summary>
    /// Gets or sets the source user ID.
    /// </summary>
    public string SourceUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source item ID.
    /// </summary>
    public string SourceItemId { get; set; } = string.Empty;
}
