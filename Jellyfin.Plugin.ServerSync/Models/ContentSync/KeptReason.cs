namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>Why a copy in a Link mapping is a copy. Stored as INTEGER in SQLite.</summary>
public enum KeptReason
{
    /// <summary>Not a kept copy, or a copy from before this was recorded.</summary>
    None = 0,

    /// <summary>Copied because an Always Keep pick covers it. Removing the pick turns it back into a link.</summary>
    Pick = 1,

    /// <summary>Downloaded by hand with Download. It stays until turned back by hand.</summary>
    Hand = 2,

    /// <summary>
    /// Already on this server when it was matched to a source's file. It is this server's own file,
    /// so it stays when the source removes its file, and is never turned into a link.
    /// </summary>
    Own = 3
}
