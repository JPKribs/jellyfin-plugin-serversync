using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Represents a mapping between a source library and a local library.
/// Used for syncing content and watch history between servers.
/// </summary>
public class LibraryMapping
{
    /// <summary>
    /// The library ID on the source server.
    /// </summary>
    public string SourceLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// The library name on the source server (for display purposes).
    /// </summary>
    public string SourceLibraryName { get; set; } = string.Empty;

    /// <summary>
    /// The root path of the library on the source server.
    /// </summary>
    public string SourceRootPath { get; set; } = string.Empty;

    /// <summary>
    /// The library ID on the local server.
    /// </summary>
    public string LocalLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// The library name on the local server (for display purposes).
    /// </summary>
    public string LocalLibraryName { get; set; } = string.Empty;

    /// <summary>
    /// The root path where files will be stored locally.
    /// </summary>
    public string LocalRootPath { get; set; } = string.Empty;

    /// <summary>
    /// Whether this mapping is currently enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// The filter mode for this library mapping.
    /// AllowAll = no filtering, Whitelist = only listed items, Blacklist = exclude listed items.
    /// </summary>
    public LibraryFilterMode FilterMode { get; set; } = LibraryFilterMode.AllowAll;

    /// <summary>
    /// Items selected for whitelist/blacklist filtering.
    /// Only used when <see cref="FilterMode"/> is Whitelist or Blacklist.
    /// Each item's path is used for child matching, selecting a series filters all its episodes.
    /// </summary>
    public List<FilteredItem> FilteredItems { get; set; } = new();
}
