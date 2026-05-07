using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>
/// Standard response shape for bulk-status endpoints. Surfaces per-item
/// outcomes so the UI can flag partial failures instead of treating any
/// non-zero count as success. <see cref="Updated"/> rows in <see cref="Requested"/>
/// total; <see cref="Failed"/> lists IDs that didn't update (typically:
/// row was deleted between the user's click and the request landing).
/// </summary>
public sealed class BulkOperationResult
{
    /// <summary>Number of rows actually updated.</summary>
    public int Updated { get; set; }

    /// <summary>Number of IDs the caller requested.</summary>
    public int Requested { get; set; }

    /// <summary>Per-item failures with diagnostic reason. Empty on full success.</summary>
    public IReadOnlyList<BulkOperationFailure> Failed { get; set; } = new List<BulkOperationFailure>();
}

/// <summary>One failed item from a bulk operation.</summary>
public sealed class BulkOperationFailure
{
    /// <summary>The row ID that didn't update.</summary>
    public long Id { get; set; }

    /// <summary>Human-readable reason.</summary>
    public string Reason { get; set; } = string.Empty;
}
