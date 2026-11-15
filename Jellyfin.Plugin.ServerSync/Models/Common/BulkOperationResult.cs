using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>
/// The response of a bulk status route, listing each row that did not update so the page can flag a partial failure.
/// </summary>
public sealed class BulkOperationResult
{
    /// <summary>Gets or sets how many rows were updated.</summary>
    public int Updated { get; set; }

    /// <summary>Gets or sets how many IDs the caller asked for.</summary>
    public int Requested { get; set; }

    /// <summary>Gets or sets the rows that did not update, each with its reason.</summary>
    public IReadOnlyList<BulkOperationFailure> Failed { get; set; } = new List<BulkOperationFailure>();
}
