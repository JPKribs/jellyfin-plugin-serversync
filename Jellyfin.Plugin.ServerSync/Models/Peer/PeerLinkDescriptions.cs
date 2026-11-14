using System.Collections.Generic;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// Asks the source about files a server holds links to: whether each is still there, and, when
/// <see cref="Details"/> is set, its media info, so a link shows its runtime, codecs, and tracks
/// without being probed.
/// </summary>
public class PeerLinkDescribeRequest
{
    /// <summary>The most items one request may name.</summary>
    public const int MaxItems = 200;

    /// <summary>Gets or sets the asking server's Jellyfin id.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the source's ids of the items.</summary>
    public List<string> ItemIds { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether each answer carries the file's media info.</summary>
    public bool Details { get; set; }
}

/// <summary>The source's answer to a <see cref="PeerLinkDescribeRequest"/>.</summary>
public class PeerLinkDescribeResponse
{
    /// <summary>Gets or sets one answer per item asked about.</summary>
    public List<PeerLinkDescription> Items { get; set; } = new();
}

/// <summary>What the source holds for one linked file.</summary>
public class PeerLinkDescription
{
    /// <summary>
    /// The reason a source gives when the file is still its item but could not be read just now. Unlike
    /// every other reason, it does not say the file is gone.
    /// </summary>
    public const string Unreadable = "the source cannot read the file";

    /// <summary>Gets or sets the source's id of the item.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the file is there to stream.</summary>
    public bool Exists { get; set; }

    /// <summary>Gets or sets why the file cannot be streamed, when it cannot.</summary>
    public string? Reason { get; set; }

    /// <summary>Gets or sets the file's size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets a value indicating whether the media info below was filled in.</summary>
    public bool HasDetails { get; set; }

    /// <summary>Gets or sets the runtime.</summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>Gets or sets the container.</summary>
    public string? Container { get; set; }

    /// <summary>Gets or sets the total bitrate.</summary>
    public int? TotalBitrate { get; set; }

    /// <summary>Gets or sets the streams inside the file. External files beside it are left out.</summary>
    public List<MediaStream> Streams { get; set; } = new();

    /// <summary>Gets or sets the attachments inside the file, such as subtitle fonts.</summary>
    public List<MediaAttachment> Attachments { get; set; } = new();

    /// <summary>Gets or sets the chapters.</summary>
    public List<PeerChapter> Chapters { get; set; } = new();

    /// <summary>Gets or sets the trickplay image sets the source holds for the file.</summary>
    public List<PeerTrickplay> Trickplay { get; set; } = new();
}

/// <summary>One chapter of a linked file.</summary>
public class PeerChapter
{
    /// <summary>Gets or sets where the chapter starts.</summary>
    public long StartPositionTicks { get; set; }

    /// <summary>Gets or sets the chapter's name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets a value indicating whether the source holds an image for the chapter.</summary>
    public bool HasImage { get; set; }
}

/// <summary>One set of trickplay images the source holds for a linked file.</summary>
public class PeerTrickplay
{
    /// <summary>Gets or sets the width of one thumbnail.</summary>
    public int Width { get; set; }

    /// <summary>Gets or sets the height of one thumbnail.</summary>
    public int Height { get; set; }

    /// <summary>Gets or sets how many thumbnails a tile holds across.</summary>
    public int TileWidth { get; set; }

    /// <summary>Gets or sets how many thumbnails a tile holds down.</summary>
    public int TileHeight { get; set; }

    /// <summary>Gets or sets how many thumbnails there are in all.</summary>
    public int ThumbnailCount { get; set; }

    /// <summary>Gets or sets the time between thumbnails, in milliseconds.</summary>
    public int Interval { get; set; }

    /// <summary>Gets or sets the peak bandwidth, in bits per second.</summary>
    public int Bandwidth { get; set; }

    /// <summary>How many tile images the set is made of.</summary>
    /// <returns>The count.</returns>
    public int TileCount()
    {
        var perTile = TileWidth * TileHeight;
        return perTile <= 0 || ThumbnailCount <= 0 ? 0 : (ThumbnailCount + perTile - 1) / perTile;
    }
}
