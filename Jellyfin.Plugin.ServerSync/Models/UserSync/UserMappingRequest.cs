using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.UserSync;

/// <summary>
/// Request model for identifying a single user mapping.
/// </summary>
public class UserMappingRequest
{
    /// <summary>
    /// Gets or sets the source server user ID.
    /// </summary>
    public string SourceUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local server user ID.
    /// </summary>
    public string LocalUserId { get; set; } = string.Empty;
}
