using System.Collections.Generic;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

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
