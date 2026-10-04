namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Defines how library content filtering works.
/// </summary>
public enum LibraryFilterMode
{
    /// <summary>Default, no filtering, all items synced.</summary>
    AllowAll = 0,

    /// <summary>Only items in FilteredItems are synced. Everything else is blocked.</summary>
    Whitelist = 1,

    /// <summary>Items in FilteredItems are blocked. Everything else is synced.</summary>
    Blacklist = 2
}
