using System;

namespace Jellyfin.Plugin.ServerSync.Models.HistorySync;

/// <summary>
/// The parts of a user's watch history for one item that History Sync may copy. A part that is
/// switched off is still read, so the rest merges as it would otherwise, but it is never written on
/// this server, never proposed to another, and never counts as a difference.
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
