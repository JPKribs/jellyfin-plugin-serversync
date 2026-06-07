using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

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

    [Fact]
    public void CarryForwardSizes_DoesNotFill_WhenTagChanged()
    {
        var fresh = Manifest(("Primary", 0, "new-tag"));
        var prior = Manifest(("Primary", 164065, "old-tag"));

        var merged = First(ImageManifestEnricher.CarryForwardSizes(fresh, prior)!, "Primary");

        Assert.Equal(0, merged.Size);
    }

    [Fact]
    public void CarryForwardSizes_LeavesAlreadyMeasuredSizeAlone()
    {
        var fresh = Manifest(("Primary", 999, "abc"));
        var prior = Manifest(("Primary", 164065, "abc"));

        var merged = First(ImageManifestEnricher.CarryForwardSizes(fresh, prior)!, "Primary");

        Assert.Equal(999, merged.Size);
    }

    [Fact]
    public void CarryForwardSizes_DoesNotFill_WhenTagMissing()
    {
        var fresh = Manifest(("Primary", 0, null));
        var prior = Manifest(("Primary", 164065, null));

        var merged = First(ImageManifestEnricher.CarryForwardSizes(fresh, prior)!, "Primary");

        Assert.Equal(0, merged.Size);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CarryForwardSizes_ReturnsFreshUnchanged_WhenPriorEmpty(string? prior)
    {
        var fresh = Manifest(("Primary", 0, "abc"));

        Assert.Equal(fresh, ImageManifestEnricher.CarryForwardSizes(fresh, prior));
    }
}
