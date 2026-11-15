using Jellyfin.Plugin.ServerSync.Models.Common;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Models.Common;

/// <summary>
/// An image's size and dimensions read as short text a person can compare at a glance.
/// </summary>
public sealed class ImageInfoDtoTests
{
    /// <summary>
    /// The size is shown in bytes below 1024, in KB below 1024 KB, and in MB from there up, to one decimal.
    /// True: each size lands in the unit a reader expects at each boundary.
    /// False: an image would show under the wrong unit and look a thousand times off.
    /// </summary>
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048575L, "1024.0 KB")]
    [InlineData(1048576L, "1.0 MB")]
    [InlineData(1572864L, "1.5 MB")]
    [InlineData(1073741824L, "1024.0 MB")]
    public void FormattedSize_EachUnitBoundary_PicksTheUnit(long size, string expected)
    {
        var image = new ImageInfoDto { Size = size };

        var text = image.FormattedSize;

        Assert.Equal(expected, text);
    }

    /// <summary>
    /// The dimensions are shown as width, an x, then height.
    /// True: a 1920 by 1080 image reads as 1920x1080.
    /// False: width and height would swap and a portrait image would read as landscape.
    /// </summary>
    [Theory]
    [InlineData(1920, 1080, "1920x1080")]
    [InlineData(0, 0, "0x0")]
    [InlineData(int.MaxValue, 1, "2147483647x1")]
    public void FormattedDimensions_WidthAndHeight_JoinsWithAnX(int width, int height, string expected)
    {
        var image = new ImageInfoDto { Width = width, Height = height };

        var text = image.FormattedDimensions;

        Assert.Equal(expected, text);
    }

    /// <summary>
    /// A new image has an empty type, no tag, and zero for every number.
    /// True: a manifest entry with a field left out reads as empty, not as a null type.
    /// False: reading the type of a sparse manifest entry would throw.
    /// </summary>
    [Fact]
    public void Constructor_NoValues_StartsEmpty()
    {
        var image = new ImageInfoDto();

        Assert.Equal(string.Empty, image.ImageType);
        Assert.Null(image.Tag);
        Assert.Equal(0, image.ImageIndex);
        Assert.Equal(0L, image.Size);
    }
}
