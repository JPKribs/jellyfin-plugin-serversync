using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;

/// <summary>
/// Library information for API responses.
/// </summary>
public class LibraryDto
{
    /// <summary>Gets or sets the library id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the library name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the library's folders.</summary>
    public List<string> Locations { get; set; } = new();
}
