namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>One failed item from a bulk operation.</summary>
public sealed class BulkOperationFailure
{
    /// <summary>Gets or sets the ID of the row that did not update.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets why the row did not update.</summary>
    public string Reason { get; set; } = string.Empty;
}
