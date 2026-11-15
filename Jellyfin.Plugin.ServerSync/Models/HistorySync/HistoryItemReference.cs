namespace Jellyfin.Plugin.ServerSync.Models.HistorySync;

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
