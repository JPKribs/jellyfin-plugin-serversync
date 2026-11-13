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

/// <summary>Why a copy in a Link mapping is a copy. Stored as INTEGER in SQLite.</summary>
public enum KeptReason
{
    /// <summary>Not a kept copy, or a copy from before this was recorded.</summary>
    None = 0,

    /// <summary>Copied because an Always Keep pick covers it. Removing the pick turns it back into a link.</summary>
    Pick = 1,

    /// <summary>Downloaded by hand with Download. It stays until turned back by hand.</summary>
    Hand = 2
}

/// <summary>Helpers for <see cref="StorageMode"/>.</summary>
public static class StorageModes
{
    /// <summary>Whether the file here is a .strm link, including one on its way to a copy.</summary>
    public static bool IsLink(this StorageMode mode) => mode is StorageMode.Link or StorageMode.Promoting;
}
