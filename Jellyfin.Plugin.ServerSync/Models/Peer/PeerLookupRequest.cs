using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// Asks a peer to find the item on its side that matches one held here.
/// </summary>
public class PeerLookupRequest
{
    /// <summary>Gets or sets the item's path, tried first.</summary>
    public string? Path { get; set; }

    /// <summary>Gets or sets the item's Jellyfin type.</summary>
    public string? ItemType { get; set; }

    /// <summary>Gets or sets the item's name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the item's provider ids.</summary>
    public Dictionary<string, string>? ProviderIds { get; set; }

    /// <summary>Gets or sets the provider ids of an episode's or season's series.</summary>
    public Dictionary<string, string>? SeriesProviderIds { get; set; }

    /// <summary>Gets or sets the item's index, such as the episode number.</summary>
    public int? IndexNumber { get; set; }

    /// <summary>Gets or sets the index of the item's parent, such as the season number.</summary>
    public int? ParentIndexNumber { get; set; }
}
