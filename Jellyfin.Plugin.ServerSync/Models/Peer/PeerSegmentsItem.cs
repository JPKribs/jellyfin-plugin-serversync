using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// The media segments a peer holds for one of its items, with the size and runtime of the item's file so the caller uses them only for the same file.
/// </summary>
public class PeerSegmentsItem
{
    /// <summary>Gets or sets the item's id on the peer, as the request named it.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the peer holds the item, as a video or audio file the caller may see.</summary>
    public bool Found { get; set; }

    /// <summary>Gets or sets the size of the item's file in bytes, or null when the peer does not know it.</summary>
    public long? Size { get; set; }

    /// <summary>Gets or sets how long the item's file runs, in ticks, or null when the peer does not know it.</summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>Gets or sets the Jellyfin id of the server the segments were first made on, or null when they were made on the peer itself.</summary>
    public string? OriginServerId { get; set; }

    /// <summary>Gets the segments the peer holds for the item.</summary>
    public List<PeerSegment> Segments { get; init; } = new();
}
