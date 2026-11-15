namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// How a mapped library holds the source's files. Stored as INTEGER in SQLite.
/// </summary>
public enum StorageMode
{
    /// <summary>A full copy of each file is downloaded.</summary>
    Copy = 0,

    /// <summary>A small .strm link is written for each file, and playback streams from the source.</summary>
    Link = 1,

    /// <summary>
    /// A link being turned into a full copy. It stays a link, and plays as one, until the copy has
    /// downloaded, and then the .strm is removed.
    /// </summary>
    Promoting = 2,

    /// <summary>
    /// A kept copy going back to a link, because its Always Keep pick was removed or it was turned back
    /// by hand. It stays a copy, and plays as one, until the link is written, and then the copy moves
    /// into the link cache.
    /// </summary>
    Unkeeping = 3
}
