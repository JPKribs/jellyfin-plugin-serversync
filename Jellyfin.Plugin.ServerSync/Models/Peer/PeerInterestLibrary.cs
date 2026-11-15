using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Utilities;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// One mapped library in a <see cref="PeerInterest"/>.
/// </summary>
public class PeerInterestLibrary
{
    /// <summary>Gets or sets the library's root folder on the peer.</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>Gets or sets the Library Filter mode.</summary>
    public LibraryFilterMode FilterMode { get; set; }

    /// <summary>Gets or sets the paths the Library Filter lists, in the peer's folders.</summary>
    public List<string> Filtered { get; set; } = new();
}
