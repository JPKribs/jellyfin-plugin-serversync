using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Pairs a library on a source with the library on this server it fills.
/// </summary>
public class LibraryMapping
{
    /// <summary>
    /// Gets or sets the library's ID on the source.
    /// </summary>
    public string SourceLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library's name on the source, for display.
    /// </summary>
    public string SourceLibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library's root folder on the source.
    /// </summary>
    public string SourceRootPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library's ID on this server.
    /// </summary>
    public string LocalLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library's name on this server, for display.
    /// </summary>
    public string LocalLibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the root folder files are stored under on this server.
    /// </summary>
    public string LocalRootPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this library mapping takes part in syncing.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the Library Filter mode, which allows everything, only the listed items, or all but the listed items.
    /// </summary>
    public LibraryFilterMode FilterMode { get; set; } = LibraryFilterMode.AllowAll;

    /// <summary>
    /// Gets or sets the items a Whitelist or Blacklist lists. Listing a series covers every episode under its path.
    /// </summary>
    public List<FilteredItem> FilteredItems { get; set; } = new();

    /// <summary>
    /// Gets or sets the Storage, a full Copy or a Link that streams from the source.
    /// It is left out of JSON while it is Copy, so a mapping without links keeps the hash it always had.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ContentSync.StorageMode StorageMode { get; set; } = ContentSync.StorageMode.Copy;

    /// <summary>
    /// Gets or sets a value indicating whether this library's links are kept out of the link cache.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExcludeFromLinkCache { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether History in this library is merged and written to both servers.
    /// Only the Sync direction writes back, so it has no effect in any other.
    /// </summary>
    public bool NegotiateHistory { get; set; }

    /// <summary>
    /// Gets or sets the shows and films a Link library holds as permanent copies, per <see cref="Services.Links.AlwaysKeep"/>.
    /// It is left out of JSON while empty, so a mapping that picked nothing keeps the hash it always had.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FilteredItem>? PermanentItems { get; set; }
}
