using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

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
