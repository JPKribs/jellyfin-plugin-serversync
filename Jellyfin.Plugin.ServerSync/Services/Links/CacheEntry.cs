using System;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>One file in the link cache.</summary>
public sealed class CacheEntry
{
    /// <summary>Gets or sets the link's id.</summary>
    public string LinkId { get; set; } = string.Empty;

    /// <summary>Gets or sets the file's name inside the cache.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>Gets or sets the file's size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets when the file was cached.</summary>
    public DateTime CachedAt { get; set; }

    /// <summary>Gets or sets when the link was last played, if it has been since it was cached.</summary>
    public DateTime? LastPlayedAt { get; set; }

    /// <summary>Gets or sets why the file was cached.</summary>
    public CacheReason Reason { get; set; }
}
