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
    Promoting = 2
}

/// <summary>Helpers for <see cref="StorageMode"/>.</summary>
public static class StorageModes
{
    /// <summary>Whether the file here is a .strm link, including one on its way to a copy.</summary>
    public static bool IsLink(this StorageMode mode) => mode is StorageMode.Link or StorageMode.Promoting;
}
