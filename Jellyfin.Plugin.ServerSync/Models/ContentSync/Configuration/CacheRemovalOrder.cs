namespace Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;

/// <summary>Which cached file is dropped first when the link cache is full.</summary>
public enum CacheRemovalOrder
{
    /// <summary>The file played longest ago, or never played and cached longest ago.</summary>
    LastPlayed = 0,

    /// <summary>The file cached longest ago.</summary>
    Oldest = 1,

    /// <summary>The file cached most recently.</summary>
    Newest = 2,

    /// <summary>The largest file.</summary>
    Largest = 3
}
