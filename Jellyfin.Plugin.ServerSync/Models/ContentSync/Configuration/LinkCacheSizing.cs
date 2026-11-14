namespace Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;

/// <summary>How much disk the link cache may use.</summary>
public enum LinkCacheSizing
{
    /// <summary>The cache is off and holds nothing.</summary>
    Off = 0,

    /// <summary>All the room on the cache's disk, less the free space that is always kept.</summary>
    All = 1,

    /// <summary>Up to a set number of gigabytes.</summary>
    Fixed = 2,

    /// <summary>A share of the room on the cache's disk.</summary>
    Percent = 3
}
