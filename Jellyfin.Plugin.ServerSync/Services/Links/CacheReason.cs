namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>Why a file is in the link cache.</summary>
public enum CacheReason
{
    /// <summary>Someone played the link.</summary>
    Played = 0,

    /// <summary>It is an episode ahead of the one a user is watching.</summary>
    Ahead = 1,

    /// <summary>It was a kept copy, moved here when it went back to being a link.</summary>
    Unkept = 2
}
