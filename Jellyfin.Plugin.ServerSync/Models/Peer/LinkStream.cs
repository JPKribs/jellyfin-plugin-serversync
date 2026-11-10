namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>Asks a source whether a user on the asking server may stream one of its files through a link.</summary>
public class LinkStreamCheckRequest
{
    /// <summary>Gets or sets the asking server's Jellyfin id.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the source's id of the item. Empty when the item is named by <see cref="Path"/>.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file's path on the source, for a server that holds no row for it: a link from
    /// another server falling back to this one, which holds the same file.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>Gets or sets a value indicating whether this is the asking server's media probe, with no user.</summary>
    public bool Probe { get; set; }

    /// <summary>Gets or sets the user's id on the asking server.</summary>
    public string? UserId { get; set; }

    /// <summary>Gets or sets the source user the asking server maps that user to, if it maps them.</summary>
    public string? SourceUserId { get; set; }
}

/// <summary>The source's answer to a <see cref="LinkStreamCheckRequest"/>.</summary>
public class LinkStreamCheckResponse
{
    /// <summary>Gets or sets a value indicating whether the stream is allowed.</summary>
    public bool Allowed { get; set; }

    /// <summary>Gets or sets why it is refused, worded for the person trying to play.</summary>
    public string? Reason { get; set; }

    /// <summary>Gets or sets the source's id of the item, when allowed.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets the file's size in bytes, when allowed, so the asking server can tell it is the same file.</summary>
    public long Size { get; set; }
}
