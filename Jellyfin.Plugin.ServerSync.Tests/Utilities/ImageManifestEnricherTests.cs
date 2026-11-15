using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

/// <summary>
/// The image manifest enricher carries measured sizes forward so unchanged images are not measured again.
/// </summary>
public class ImageManifestEnricherTests
{
    private static string Manifest(params (string Type, long Size, string? Tag)[] images)
    {
        var map = new Dictionary<string, List<ImageInfoDto>>();
        foreach (var img in images)
        {
            if (!map.TryGetValue(img.Type, out var list))
            {
                list = new List<ImageInfoDto>();
                map[img.Type] = list;
            }

            list.Add(new ImageInfoDto
            {
                ImageType = img.Type,
                Size = img.Size,
                Width = img.Size > 0 ? 100 : 0,
                Height = img.Size > 0 ? 200 : 0,
                Tag = img.Tag
            });
        }

        return JsonSerializer.Serialize(map);
    }

    private static ImageInfoDto First(string json, string type)
        => JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(json)![type][0];

    /// <summary>
    /// A fresh image that has no size takes the size from the prior manifest when its tag is the same.
    /// True: an image that did not change is not measured again.
    /// False: every refresh would measure every image again.
    /// </summary>
    [Fact]
    public void CarryForwardSizes_FillsSize_WhenTagUnchanged()
    {
        var fresh = Manifest(("Primary", 0, "abc"));
        var prior = Manifest(("Primary", 164065, "abc"));

        var merged = First(ImageManifestEnricher.CarryForwardSizes(fresh, prior)!, "Primary");

        Assert.Equal(164065, merged.Size);
        Assert.Equal(100, merged.Width);
        Assert.Equal(200, merged.Height);
    }

    /// <summary>
    /// A fresh image whose tag changed does not take the prior size.
    /// True: a replaced image is measured again.
    /// False: a new image would be reported with the old image's size.
    /// </summary>
    [Fact]
    public void CarryForwardSizes_DoesNotFill_WhenTagChanged()
    {
        var fresh = Manifest(("Primary", 0, "new-tag"));
        var prior = Manifest(("Primary", 164065, "old-tag"));

        var merged = First(ImageManifestEnricher.CarryForwardSizes(fresh, prior)!, "Primary");

        Assert.Equal(0, merged.Size);
    }

    /// <summary>
    /// A fresh image that already has a size keeps it.
    /// True: a measured size is never overwritten by an older one.
    /// False: a fresh measurement would be replaced by a stale size.
    /// </summary>
    [Fact]
    public void CarryForwardSizes_LeavesAlreadyMeasuredSizeAlone()
    {
        var fresh = Manifest(("Primary", 999, "abc"));
        var prior = Manifest(("Primary", 164065, "abc"));

        var merged = First(ImageManifestEnricher.CarryForwardSizes(fresh, prior)!, "Primary");

        Assert.Equal(999, merged.Size);
    }

    /// <summary>
    /// Images with no tag never take a prior size.
    /// True: an image that cannot be told apart from an older one is measured again.
    /// False: an untagged image could carry another image's size.
    /// </summary>
    [Fact]
    public void CarryForwardSizes_DoesNotFill_WhenTagMissing()
    {
        var fresh = Manifest(("Primary", 0, null));
        var prior = Manifest(("Primary", 164065, null));

        var merged = First(ImageManifestEnricher.CarryForwardSizes(fresh, prior)!, "Primary");

        Assert.Equal(0, merged.Size);
    }

    /// <summary>
    /// With no prior manifest the fresh manifest comes back unchanged.
    /// True: the first scan of an item needs no prior state.
    /// False: a first scan would throw or lose images.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CarryForwardSizes_ReturnsFreshUnchanged_WhenPriorEmpty(string? prior)
    {
        var fresh = Manifest(("Primary", 0, "abc"));

        Assert.Equal(fresh, ImageManifestEnricher.CarryForwardSizes(fresh, prior));
    }
}
