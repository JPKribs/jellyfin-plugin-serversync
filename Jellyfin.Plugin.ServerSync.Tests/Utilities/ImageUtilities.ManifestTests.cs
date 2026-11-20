using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using LocalImageType = MediaBrowser.Model.Entities.ImageType;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

/// <summary>
/// Image manifests are built the same way for every module, and measured sizes are carried forward so unchanged images are not measured again.
/// </summary>
public class ImageUtilitiesManifestTests
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

        var merged = First(ImageUtilities.CarryForwardSizes(fresh, prior)!, "Primary");

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

        var merged = First(ImageUtilities.CarryForwardSizes(fresh, prior)!, "Primary");

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

        var merged = First(ImageUtilities.CarryForwardSizes(fresh, prior)!, "Primary");

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

        var merged = First(ImageUtilities.CarryForwardSizes(fresh, prior)!, "Primary");

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

        Assert.Equal(fresh, ImageUtilities.CarryForwardSizes(fresh, prior));
    }

    /// <summary>
    /// A source item with an image tag gets a manifest.
    /// True: the scan sees that the source holds an image.
    /// False: the image would never be compared or synced.
    /// </summary>
    [Fact]
    public void BuildSourceManifest_SourceWithImageTag_ReturnsAManifest()
    {
        var dto = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "person",
            ImageTags = new BaseItemDto_ImageTags
            {
                AdditionalData = new Dictionary<string, object>
                {
                    ["Primary"] = "image-tag-abc"
                }
            }
        };

        var src = ImageUtilities.BuildSourceManifest(dto);

        Assert.NotNull(src);
    }

    /// <summary>
    /// Several BackdropImageTags become one Backdrop entry per tag, indexed by position.
    /// True: the apply downloads and writes every source backdrop instead of just the first.
    /// False: a single-entry Backdrop manifest would silently drop the rest and never report the variance.
    /// </summary>
    [Fact]
    public void BuildSourceManifest_SourceWithMultipleBackdrops_EmitsOneEntryPerTag()
    {
        var dto = Source(d => d.BackdropImageTags = new List<string> { "bd-0", "bd-1", "bd-2" });

        var src = ImageUtilities.BuildSourceManifest(dto);

        Assert.NotNull(src);
        var manifest = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(src!);
        var backdrops = manifest!["Backdrop"];

        Assert.Equal(3, backdrops.Count);
        Assert.Equal(new[] { 0, 1, 2 }, backdrops.ConvertAll(b => b.ImageIndex));
        Assert.Equal(new[] { "bd-0", "bd-1", "bd-2" }, backdrops.ConvertAll(b => b.Tag));
    }

    /// <summary>
    /// Backdrops and a Primary tag coexist in the same source manifest.
    /// True: a person with both syncs both, and the comparator sees each type's real count.
    /// False: one type overwriting the other would queue an apply that can never verify.
    /// </summary>
    [Fact]
    public void BuildSourceManifest_SourceWithPrimaryAndBackdrops_KeepsBothTypes()
    {
        var dto = Source(d =>
        {
            d.ImageTags = new BaseItemDto_ImageTags
            {
                AdditionalData = new Dictionary<string, object> { ["Primary"] = "primary-tag" }
            };
            d.BackdropImageTags = new List<string> { "bd-0", "bd-1" };
        });

        var src = ImageUtilities.BuildSourceManifest(dto);

        var manifest = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(src!);

        Assert.Single(manifest!["Primary"]);
        Assert.Equal(2, manifest["Backdrop"].Count);
    }

    /// <summary>
    /// DTO without ImageTags returns null source-image manifest.
    /// True: HasImagesChanges treats null as nothing to sync.
    /// False: a non-null but empty manifest could trigger a no-op apply that then fails verify.
    /// </summary>
    [Fact]
    public void BuildSourceManifest_SourceWithNoImageTags_ReturnsNullSource()
    {
        var dto = Source();

        var src = ImageUtilities.BuildSourceManifest(dto);

        Assert.Null(src);
    }

    /// <summary>
    /// The image types a caller names are left out of the source manifest, in any letter case.
    /// True: a type an item cannot hold is never tracked.
    /// False: such a type would queue an apply that can never verify.
    /// </summary>
    [Fact]
    public void BuildSourceManifest_WithoutTypes_LeavesThemOut()
    {
        var dto = Source(d => d.ImageTags = new BaseItemDto_ImageTags
        {
            AdditionalData = new Dictionary<string, object> { ["Primary"] = "p", ["Profile"] = "x" }
        });

        var all = Types(ImageUtilities.BuildSourceManifest(dto));
        var without = Types(ImageUtilities.BuildSourceManifest(dto, new[] { "profile" }));

        Assert.Equal(new[] { "Primary", "Profile" }, all);
        Assert.Equal(new[] { "Primary" }, without);
    }

    /// <summary>
    /// The local manifest lists the image types asked for, in order, with each file's size.
    /// True: a module compares only the types it syncs, by their real sizes.
    /// False: a type the module does not sync would show as a difference, or every size would read as 0.
    /// </summary>
    [Fact]
    public void BuildLocalManifest_ItemWithImages_ListsTheAskedTypesWithSizes()
    {
        using var temp = new SyncTempFolder("image-manifest");
        var item = new Movie { Id = Guid.NewGuid(), Name = "Film" };
        item.ImageInfos = new[]
        {
            Image(temp, LocalImageType.Primary, "primary", 5),
            Image(temp, LocalImageType.Backdrop, "first", 7),
            Image(temp, LocalImageType.Backdrop, "second", 9),
            Image(temp, LocalImageType.Logo, "logo", 3)
        };

        var manifest = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(
            ImageUtilities.BuildLocalManifest(item, new[] { LocalImageType.Primary, LocalImageType.Backdrop })!)!;

        Assert.Equal(new[] { "Primary", "Backdrop" }, manifest.Keys);
        Assert.Equal(5, Assert.Single(manifest["Primary"]).Size);
        Assert.Equal(new[] { (0, 7L), (1, 9L) }, manifest["Backdrop"].Select(b => (b.ImageIndex, b.Size)));
    }

    /// <summary>
    /// An item with none of the asked image types has no manifest, and a missing file keeps a size of 0.
    /// True: an item without images reads as nothing to compare.
    /// False: an empty manifest would differ from a null one and queue the row.
    /// </summary>
    [Fact]
    public void BuildLocalManifest_NoImagesOrMissingFile_IsNullOrSizedZero()
    {
        var bare = new Movie { Id = Guid.NewGuid(), Name = "Film" };
        var missing = new Movie { Id = Guid.NewGuid(), Name = "Film" };
        missing.ImageInfos = new[] { new ItemImageInfo { Type = LocalImageType.Primary, Path = "/nowhere/primary.jpg" } };

        Assert.Null(ImageUtilities.BuildLocalManifest(bare, new[] { LocalImageType.Primary }));
        Assert.Equal(0, First(ImageUtilities.BuildLocalManifest(missing, new[] { LocalImageType.Primary })!, "Primary").Size);
    }

    /// <summary>
    /// Named types are removed from a manifest, and one that holds none of them or cannot be read comes back as it was.
    /// True: the check after an apply skips a type the item cannot hold.
    /// False: an unreadable manifest would throw and fail the row.
    /// </summary>
    [Fact]
    public void WithoutTypes_Manifest_RemovesOnlyTheNamedTypes()
    {
        var both = Manifest(("Primary", 1, "p"), ("Profile", 1, "x"));
        var primary = Manifest(("Primary", 1, "p"));

        Assert.Equal(new[] { "Primary" }, Types(ImageUtilities.WithoutTypes(both, new[] { "PROFILE" })));
        Assert.Same(primary, ImageUtilities.WithoutTypes(primary, new[] { "Profile" }));
        Assert.Equal("not json", ImageUtilities.WithoutTypes("not json", new[] { "Profile" }));
        Assert.Null(ImageUtilities.WithoutTypes(null, new[] { "Profile" }));
    }

    /// <summary>
    /// An image whose file has not been written again keeps its size and the source tag it was matched to, without its file being read.
    /// True: a regular scan reads no image file for an unchanged image.
    /// False: every scan would read every image file, or a matched image would lose its pair.
    /// </summary>
    [Fact]
    public void BuildLocalManifest_UnchangedImage_KeepsItsPairWithoutReadingTheFile()
    {
        using var temp = new SyncTempFolder("image-manifest");
        var item = new Movie { Id = Guid.NewGuid(), Name = "Film" };
        item.ImageInfos = new[] { Image(temp, LocalImageType.Primary, "primary", 5) };
        var first = ImageUtilities.BuildLocalManifest(item, Primary)!;
        var paired = ImageUtilities.RecordTagPairs(Manifest(("Primary", 5, "source-tag")), first)!;
        File.Delete(item.ImageInfos[0].Path);

        var again = First(ImageUtilities.BuildLocalManifest(item, Primary, paired)!, "Primary");

        Assert.Equal("source-tag", again.SyncedTag);
        Assert.Equal(5, again.Size);
        Assert.Equal(First(first, "Primary").Tag, again.Tag);
    }

    /// <summary>
    /// An image written again gets a new mark, is measured again, and loses its pair. With no prior manifest nothing is paired.
    /// True: a local image someone replaced is compared afresh, and the deep check starts from nothing.
    /// False: a replaced image would still count as the one that was synced.
    /// </summary>
    [Fact]
    public void BuildLocalManifest_ImageWrittenAgain_LosesItsPair()
    {
        using var temp = new SyncTempFolder("image-manifest");
        var item = new Movie { Id = Guid.NewGuid(), Name = "Film" };
        item.ImageInfos = new[] { Image(temp, LocalImageType.Primary, "primary", 5) };
        var first = ImageUtilities.BuildLocalManifest(item, Primary)!;
        var paired = ImageUtilities.RecordTagPairs(Manifest(("Primary", 5, "source-tag")), first)!;
        File.WriteAllBytes(item.ImageInfos[0].Path, new byte[8]);
        item.ImageInfos[0].DateModified = item.ImageInfos[0].DateModified.AddMinutes(1);

        var replaced = First(ImageUtilities.BuildLocalManifest(item, Primary, paired)!, "Primary");
        item.ImageInfos[0].DateModified = item.ImageInfos[0].DateModified.AddMinutes(-1);
        var withoutPrior = First(ImageUtilities.BuildLocalManifest(item, Primary)!, "Primary");

        Assert.Null(replaced.SyncedTag);
        Assert.Equal(8, replaced.Size);
        Assert.NotEqual(First(first, "Primary").Tag, replaced.Tag);
        Assert.Null(withoutPrior.SyncedTag);
    }

    /// <summary>
    /// With no client or no source id the scan manifest holds tags alone, and an item with no images has none.
    /// True: a scan that cannot ask for sizes still compares by tag.
    /// False: the scan would throw for a row whose server is not there.
    /// </summary>
    [Fact]
    public async Task ReadManifestsAsync_NoClientOrNoImages_IsTagOnlyOrNull()
    {
        var dto = Tagged("p");
        using var client = new ClientStubServer().Client();

        var noClient = await Read(dto, null, null, null, null, deep: true);
        var noId = await ImageUtilities.ReadManifestsAsync(dto, null, Primary, null, null, null, client, true, null, NullLogger.Instance, "Film", CancellationToken.None);
        var noImages = await Read(Source(), null, null, null, client, deep: true);

        Assert.Equal(0, First(noClient.Source!, "Primary").Size);
        Assert.Equal("p", First(noId.Source!, "Primary").Tag);
        Assert.Null(noImages.Source);
        Assert.Null(noClient.Local);
    }

    /// <summary>
    /// A local image matched before, whose source tag has not changed, is settled without asking the source or reading a size.
    /// True: a regular scan of an unchanged item makes no call.
    /// False: every scan would call the source for every item.
    /// </summary>
    [Fact]
    public async Task ReadManifestsAsync_PairedAndTagUnchanged_MatchesWithoutAsking()
    {
        using var temp = new SyncTempFolder("image-manifest");
        var (item, paired) = PairedItem(temp, "p");
        var server = new ClientStubServer();
        using var client = server.Client();

        var (source, local) = await Read(Tagged("p"), item, Manifest(("Primary", 0, "p")), paired, client, deep: false);

        Assert.Empty(server.Seen);
        Assert.Null(ImageUtilities.DescribeManifestDifference(source, local));
        Assert.Equal(0, First(source!, "Primary").Size);
    }

    /// <summary>
    /// A source tag that changed, for an image matched before, differs without asking the source.
    /// True: a new source image is fetched because its tag changed.
    /// False: a picture replaced by one of the same size would never sync.
    /// </summary>
    [Fact]
    public async Task ReadManifestsAsync_PairedAndTagChanged_DiffersWithoutAsking()
    {
        using var temp = new SyncTempFolder("image-manifest");
        var (item, paired) = PairedItem(temp, "old");
        var server = new ClientStubServer();
        using var client = server.Client();

        var (source, local) = await Read(Tagged("new"), item, Manifest(("Primary", 5, "old")), paired, client, deep: false);

        Assert.Empty(server.Seen);
        Assert.Equal("type Primary: the source image changed since it was last matched", ImageUtilities.DescribeManifestDifference(source, local));
    }

    /// <summary>
    /// An image never matched is compared by size: the source is asked when its size is not known, and a size that matches records the pair.
    /// True: pictures already on both servers are matched once without being downloaded.
    /// False: every picture would be downloaded on the first scan.
    /// </summary>
    [Fact]
    public async Task ReadManifestsAsync_NeverMatched_ComparesBySizeAndRecordsThePair()
    {
        using var temp = new SyncTempFolder("image-manifest");
        var item = new Movie { Id = Guid.NewGuid(), Name = "Film" };
        item.ImageInfos = new[] { Image(temp, LocalImageType.Primary, "primary", 5) };
        var server = new ClientStubServer();
        using var client = server.Client();

        var unsized = await Read(Tagged("p"), item, null, null, client, deep: false);
        var asked = server.Seen.Count;
        var sized = await Read(Tagged("p"), item, Manifest(("Primary", 5, "p")), null, client, deep: false);
        var otherSize = await Read(Tagged("p"), item, Manifest(("Primary", 9, "p")), null, client, deep: false);

        Assert.True(asked > 0);
        Assert.Null(First(unsized.Local!, "Primary").SyncedTag);
        Assert.Equal(asked, server.Seen.Count);
        Assert.Equal("p", First(sized.Local!, "Primary").SyncedTag);
        Assert.Null(First(otherSize.Local!, "Primary").SyncedTag);
        Assert.NotNull(ImageUtilities.DescribeManifestDifference(otherSize.Source, otherSize.Local));
    }

    /// <summary>
    /// The deep check asks the source even for an image matched before, and compares by size whatever the tag says.
    /// True: Deep image verification catches a picture swapped on disk under its old tag.
    /// False: the deep check would be no deeper than the regular one.
    /// </summary>
    [Fact]
    public async Task ReadManifestsAsync_Deep_AsksAndComparesBySize()
    {
        using var temp = new SyncTempFolder("image-manifest");
        var (item, paired) = PairedItem(temp, "p");
        var server = new ClientStubServer();
        using var client = server.Client();

        var same = await Read(Tagged("p"), item, Manifest(("Primary", 5, "p")), paired, client, deep: true);
        var swapped = await Read(Tagged("p"), item, Manifest(("Primary", 9, "p")), paired, client, deep: true);

        Assert.NotEmpty(server.Seen);
        Assert.Null(ImageUtilities.DescribeManifestDifference(same.Source, same.Local));
        Assert.Equal("p", First(same.Local!, "Primary").SyncedTag);
        Assert.NotNull(ImageUtilities.DescribeManifestDifference(swapped.Source, swapped.Local));
        Assert.Null(First(swapped.Local!, "Primary").SyncedTag);
    }

    private static readonly LocalImageType[] Primary = { LocalImageType.Primary };

    private static BaseItemDto Tagged(string tag)
        => Source(d => d.ImageTags = new BaseItemDto_ImageTags { AdditionalData = new Dictionary<string, object> { ["Primary"] = tag } });

    private static Task<(string? Source, string? Local)> Read(BaseItemDto source, BaseItem? local, string? priorSource, string? priorLocal, Jellyfin.Plugin.ServerSync.Services.Clients.SourceServerClient? client, bool deep)
        => ImageUtilities.ReadManifestsAsync(source, local, Primary, priorSource, priorLocal, Guid.NewGuid(), client, deep, null, NullLogger.Instance, "Film", CancellationToken.None);

    /// <summary>Builds an item with one 5 byte Primary image already matched to a source tag.</summary>
    private static (Movie Item, string PairedLocal) PairedItem(SyncTempFolder temp, string sourceTag)
    {
        var item = new Movie { Id = Guid.NewGuid(), Name = "Film" };
        item.ImageInfos = new[] { Image(temp, LocalImageType.Primary, "primary", 5) };
        var paired = ImageUtilities.RecordTagPairs(Manifest(("Primary", 5, sourceTag)), ImageUtilities.BuildLocalManifest(item, Primary))!;
        return (item, paired);
    }

    private static BaseItemDto Source(Action<BaseItemDto>? configure = null)
    {
        var dto = new BaseItemDto { Id = Guid.NewGuid(), Name = "Source Person" };
        configure?.Invoke(dto);
        return dto;
    }

    private static string[] Types(string? manifest)
        => JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(manifest!)!.Keys.ToArray();

    private static ItemImageInfo Image(SyncTempFolder temp, LocalImageType type, string name, int bytes)
    {
        var path = Path.Combine(temp.Root, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return new ItemImageInfo { Type = type, Path = path, DateModified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
    }
}
