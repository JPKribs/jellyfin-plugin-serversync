using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Request for bulk item operations.
/// </summary>
public class BulkItemsRequest
{
    /// <summary>Gets or sets the source item ids the operation applies to.</summary>
    [Required]
    public List<string> SourceItemIds { get; set; } = new();
}
