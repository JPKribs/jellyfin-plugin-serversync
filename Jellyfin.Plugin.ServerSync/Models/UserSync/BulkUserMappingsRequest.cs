using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.UserSync;

/// <summary>
/// Request model for bulk operations on user mappings.
/// </summary>
public class BulkUserMappingsRequest
{
    /// <summary>
    /// Gets or sets the list of user mappings to operate on.
    /// </summary>
    public List<UserMappingRequest> UserMappings { get; set; } = new();
}
