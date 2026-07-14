namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Represents an item selected for whitelist/blacklist filtering.
/// </summary>
public class FilteredItem
{
    /// <summary>
    /// Gets or sets the item ID on the source server.
    /// </summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name of the item.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the production year (for display).
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets the source-relative path of the item (used for path-based child matching).
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item type on the source server (e.g. "BoxSet" for a
    /// collection). Collections get special handling: the collection-sync
    /// task mirrors them locally, and their expansion may span libraries.
    /// </summary>
    public string? Type { get; set; }
}
