using Jellyfin.Plugin.ServerSync.Models.Peer;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Models.Peer;

/// <summary>
/// A trickplay set never reports a tile count that would ask the source for a tile it does not hold.
/// </summary>
public sealed class PeerTrickplayTests
{
    /// <summary>
    /// A set with a negative or zero tile size, or a negative thumbnail count, has no tiles.
    /// True: a malformed set from a peer is taken as empty.
    /// False: a negative count would loop or ask for tiles below zero.
    /// </summary>
    [Theory]
    [InlineData(100, -10, 10)]
    [InlineData(100, 10, -10)]
    [InlineData(100, 10, 0)]
    [InlineData(-5, 10, 10)]
    [InlineData(0, 0, 0)]
    public void TileCount_MalformedSet_IsZero(int thumbnails, int tileWidth, int tileHeight)
    {
        var set = new PeerTrickplay { ThumbnailCount = thumbnails, TileWidth = tileWidth, TileHeight = tileHeight };

        var tiles = set.TileCount();

        Assert.Equal(0, tiles);
    }

    /// <summary>
    /// A tile that holds one thumbnail gives one tile for each thumbnail, and a tile larger than the set gives one.
    /// True: the smallest and the sparsest sets are both fetched whole.
    /// False: a one by one set would lose its last thumbnail, or a sparse set would fetch nothing.
    /// </summary>
    [Theory]
    [InlineData(1, 1, 1, 1)]
    [InlineData(7, 1, 1, 7)]
    [InlineData(1, 10, 10, 1)]
    [InlineData(99, 10, 10, 1)]
    [InlineData(200, 10, 10, 2)]
    public void TileCount_SmallestShapes_RoundsUpToWholeTiles(int thumbnails, int tileWidth, int tileHeight, int expected)
    {
        var set = new PeerTrickplay { ThumbnailCount = thumbnails, TileWidth = tileWidth, TileHeight = tileHeight };

        var tiles = set.TileCount();

        Assert.Equal(expected, tiles);
    }
}
