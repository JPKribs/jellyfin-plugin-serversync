using System;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// The five watch history fields for one user and item, as exchanged between two servers that both run
/// Server Sync. A null field means the sender has no value for it.
/// </summary>
public class PeerHistoryState
{
    /// <summary>Gets or sets whether the item is marked played.</summary>
    public bool? Played { get; set; }

    /// <summary>Gets or sets how many times the item was played.</summary>
    public int? PlayCount { get; set; }

    /// <summary>Gets or sets the resume position in ticks.</summary>
    public long? PlaybackPositionTicks { get; set; }

    /// <summary>Gets or sets when the item was last played, in UTC.</summary>
    public DateTime? LastPlayedDate { get; set; }

    /// <summary>Gets or sets whether the item is a favorite.</summary>
    public bool? IsFavorite { get; set; }
}
