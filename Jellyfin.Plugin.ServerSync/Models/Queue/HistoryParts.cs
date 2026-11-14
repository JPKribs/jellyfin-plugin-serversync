using System;

namespace Jellyfin.Plugin.ServerSync.Models.Queue;

/// <summary>
/// The parts of one user's history of one item that change on their own: whether and when it was
/// played, with its position and count, and whether it is a favorite.
/// </summary>
[Flags]
public enum HistoryParts
{
    /// <summary>Not known.</summary>
    None = 0,

    /// <summary>Played, play count, position, and last played date.</summary>
    Played = 1,

    /// <summary>Favorite.</summary>
    Favorite = 2,

    /// <summary>Both.</summary>
    All = Played | Favorite
}
