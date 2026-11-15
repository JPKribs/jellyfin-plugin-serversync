using System;

namespace Jellyfin.Plugin.ServerSync.Models.HistorySync;

/// <summary>
/// The parts of a user's History for one item that may be copied.
/// A part that is off is still read so the rest merges as usual, but it is never written, proposed, or counted as a difference.
/// </summary>
[Flags]
public enum HistoryFields
{
    /// <summary>Nothing is copied.</summary>
    None = 0,

    /// <summary>The played or unplayed mark.</summary>
    Played = 1,

    /// <summary>The resume position.</summary>
    Position = 2,

    /// <summary>The play count.</summary>
    PlayCount = 4,

    /// <summary>The date last played.</summary>
    LastPlayed = 8,

    /// <summary>The favorite mark.</summary>
    Favorite = 16,

    /// <summary>Every part.</summary>
    All = Played | Position | PlayCount | LastPlayed | Favorite
}
