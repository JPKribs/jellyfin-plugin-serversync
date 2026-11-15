using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Queue;

/// <summary>The versions a peer holds. Keys with no version are left out.</summary>
public class VersionsResponse
{
    /// <summary>Gets or sets the versions.</summary>
    public List<ObjectVersion> Items { get; set; } = new();
}
