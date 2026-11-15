using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

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
