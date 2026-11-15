namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>One failed item from a bulk operation.</summary>
public sealed class BulkOperationFailure
{
    /// <summary>The row ID that didn't update.</summary>
    public long Id { get; set; }

    /// <summary>Human-readable reason.</summary>
    public string Reason { get; set; } = string.Empty;
}
