namespace Jellyfin.Plugin.ServerSync.Models.Peer;

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

    /// <summary>Gets or sets how many link streams this server is running for the caller.</summary>
    public int StreamsInUse { get; set; }

    /// <summary>Gets or sets the most link streams this server runs for the caller at once, or 0 for no limit.</summary>
    public int StreamLimit { get; set; }
}
