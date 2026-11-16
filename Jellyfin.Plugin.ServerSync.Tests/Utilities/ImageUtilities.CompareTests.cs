using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Common.Comparators;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Jellyfin.Plugin.ServerSync.Utilities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

/// <summary>
/// Images are compared one way for every module: an item's manifests after an apply, and one picture by its hash and size.
/// </summary>
public sealed class ImageUtilitiesCompareTests : IDisposable
{
    private readonly SyncTempFolder _temp = new("image-compare");

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Matching manifests verify, and differing ones say what differs.
    /// True: a row is marked synced only when the local images match the source's.
    /// False: a half applied image set would be marked synced.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_Manifests_MatchOrSayWhatDiffers()
    {
        var same = Images(Manifest(("Primary", 0, 100)), Manifest(("Primary", 0, 100)));
        var differs = Images(Manifest(("Primary", 0, 100)), Manifest(("Primary", 0, 5)));

        var matched = await ImageUtilities.VerifyAsync(same, null, null, NullLogger.Instance, "Film", null, CancellationToken.None);
        var mismatched = await ImageUtilities.VerifyAsync(differs, null, null, NullLogger.Instance, "Film", null, CancellationToken.None);

        Assert.Equal((true, null), matched);
        Assert.False(mismatched.Succeeded);
        Assert.StartsWith("image manifest after apply does not match source manifest: ", mismatched.FailureReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The source manifest is changed by the caller before it is compared.
    /// True: an image type an item cannot hold is left out of the check.
    /// False: such a type would fail every verify.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_PrepareSource_IsComparedInPlaceOfTheSource()
    {
        var images = Images(Manifest(("Primary", 0, 100), ("Profile", 0, 50)), Manifest(("Primary", 0, 100)));

        var asIs = await ImageUtilities.VerifyAsync(images, null, null, NullLogger.Instance, "Film", null, CancellationToken.None);
        var prepared = await ImageUtilities.VerifyAsync(images, null, null, NullLogger.Instance, "Film", _ => Manifest(("Primary", 0, 100)), CancellationToken.None);

        Assert.False(asIs.Succeeded);
        Assert.True(prepared.Succeeded);
    }

    /// <summary>
    /// A local image matched to a source tag is settled by the tag, whatever the sizes say, and a changed tag differs.
    /// True: a regular scan needs no sizes for an image it matched before.
    /// False: a matched image would still need its size read on every scan.
    /// </summary>
    [Fact]
    public void DescribeManifestDifference_PairedByTag_IgnoresSizes()
    {
        var local = Paired(("Primary", 0, 100, "t1"));

        Assert.Null(ImageUtilities.DescribeManifestDifference(Tagged(("Primary", 0, 0, "t1")), local));
        Assert.Null(ImageUtilities.DescribeManifestDifference(Tagged(("Primary", 0, 999, "t1")), local));
        Assert.Equal("type Primary: the source image changed since it was last matched", ImageUtilities.DescribeManifestDifference(Tagged(("Primary", 0, 100, "t2")), local));
    }

    /// <summary>
    /// A set of images matches as a set: each source tag takes one local image, and one new tag among them differs.
    /// True: backdrops in another order still match.
    /// False: reordered backdrops would sync again on every scan.
    /// </summary>
    [Fact]
    public void DescribeManifestDifference_SeveralImages_MatchAsASet()
    {
        var local = Paired(("Backdrop", 0, 10, "a"), ("Backdrop", 1, 20, "b"));

        Assert.Null(ImageUtilities.DescribeManifestDifference(Tagged(("Backdrop", 0, 0, "b"), ("Backdrop", 1, 0, "a")), local));
        Assert.NotNull(ImageUtilities.DescribeManifestDifference(Tagged(("Backdrop", 0, 0, "a"), ("Backdrop", 1, 0, "c")), local));
        Assert.NotNull(ImageUtilities.DescribeManifestDifference(Tagged(("Backdrop", 0, 0, "a")), local));
    }

    /// <summary>
    /// Sizes are needed only when an image never matched meets a source image no one has measured.
    /// True: the source is asked for sizes once, for pictures that were on both servers before the plugin.
    /// False: the source would be asked on every scan, or never, and such pictures would never be compared.
    /// </summary>
    [Fact]
    public void NeedsSourceSizes_OnlyForAnUnmeasuredImageNeverMatched()
    {
        var unpaired = Manifest(("Primary", 0, 100));
        var paired = Paired(("Primary", 0, 100, "t1"));

        Assert.True(ImageUtilities.NeedsSourceSizes(Tagged(("Primary", 0, 0, "t1")), unpaired));
        Assert.False(ImageUtilities.NeedsSourceSizes(Tagged(("Primary", 0, 100, "t1")), unpaired));
        Assert.False(ImageUtilities.NeedsSourceSizes(Tagged(("Primary", 0, 0, "t1")), paired));
        Assert.False(ImageUtilities.NeedsSourceSizes(Tagged(("Primary", 0, 0, "t2")), paired));
        Assert.False(ImageUtilities.NeedsSourceSizes(Tagged(("Primary", 0, 0, "t1")), null));
        Assert.False(ImageUtilities.NeedsSourceSizes(Tagged(("Primary", 0, 0, "t1"), ("Primary", 1, 0, "t2")), unpaired));
    }

    /// <summary>
    /// A local image that matches a source image by size takes its tag, and a type that differs is left alone.
    /// True: the next scan compares the pair by tag.
    /// False: an image would be paired with a source image it does not match.
    /// </summary>
    [Fact]
    public void RecordTagPairs_SizeMatch_RecordsTheSourceTag()
    {
        var local = Manifest(("Primary", 0, 100), ("Logo", 0, 7));
        var source = Tagged(("Primary", 0, 100, "p"), ("Logo", 0, 9, "l"));

        var recorded = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<ImageInfoDto>>>(ImageUtilities.RecordTagPairs(source, local)!)!;

        Assert.Equal("p", recorded["Primary"][0].SyncedTag);
        Assert.Null(recorded["Logo"][0].SyncedTag);
        Assert.Same(local, ImageUtilities.RecordTagPairs(Tagged(("Primary", 0, 0, "p")), local));
        Assert.Null(ImageUtilities.RecordTagPairs(source, null));
    }

    /// <summary>
    /// A verify that passes records the pairs on the row's local manifest.
    /// True: an image just applied is compared by tag from the next scan on.
    /// False: every applied image would be measured again on the next scan.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_Match_RecordsThePairs()
    {
        var images = Images(Tagged(("Primary", 0, 100, "p")), Manifest(("Primary", 0, 100)));

        var verified = await ImageUtilities.VerifyAsync(images, null, null, NullLogger.Instance, "Film", null, CancellationToken.None);

        Assert.True(verified.Succeeded);
        Assert.Contains("\"SyncedTag\":\"p\"", images.Local, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stored hash stands only while the tag it was read under is still the source's tag.
    /// True: an unchanged picture is not downloaded again.
    /// False: a new picture would keep the old hash, or a picture never read would count as read.
    /// </summary>
    [Theory]
    [InlineData("t", "t", "hash", true)]
    [InlineData("t", "other", "hash", false)]
    [InlineData("t", "t", null, false)]
    [InlineData("t", null, "hash", false)]
    [InlineData(null, null, "hash", false)]
    [InlineData("", "", "hash", false)]
    public void PictureTagUnchanged_SameTagWithAHash_KeepsTheHash(string? tag, string? priorTag, string? priorHash, bool expected)
        => Assert.Equal(expected, ImageUtilities.PictureTagUnchanged(tag, priorTag, priorHash));

    /// <summary>
    /// A hash or a size above zero says a picture exists.
    /// True: a row with a size and no hash still counts as holding a picture.
    /// False: such a picture would be treated as removed and cleared.
    /// </summary>
    [Theory]
    [InlineData("a", null, true)]
    [InlineData(null, 4L, true)]
    [InlineData(null, 0L, false)]
    [InlineData("", null, false)]
    public void HasPicture_HashOrSize_SaysWhetherOneExists(string? hash, long? size, bool expected)
        => Assert.Equal(expected, ImageUtilities.HasPicture(hash, size));

    /// <summary>
    /// Two pictures are compared by hash when the source has one, by size when it does not, and a picture the source lacks differs while this server holds one.
    /// True: a changed, added, or removed picture queues, and a matching one does not.
    /// False: a picture would queue forever, or a changed one would never sync.
    /// </summary>
    [Theory]
    [InlineData(null, null, null, null, null)]
    [InlineData("a", 3L, "A", 9L, null)]
    [InlineData(null, 3L, null, 3L, null)]
    [InlineData(null, null, "x", null, "local still has an image after apply intended to clear it")]
    [InlineData(null, 0L, null, 4L, "local still has an image after apply intended to clear it")]
    [InlineData("a", 3L, "b", 3L, "hash mismatch after apply (source a, local b)")]
    [InlineData("a", 3L, null, null, "hash mismatch after apply (source a, local )")]
    [InlineData(null, 3L, null, 4L, "size mismatch after apply (source 3, local 4)")]
    public void DescribePictureMismatch_HashThenSize_SaysWhatDiffers(string? sourceHash, long? sourceSize, string? localHash, long? localSize, string? expected)
    {
        Assert.Equal(expected, ImageUtilities.DescribePictureMismatch(sourceHash, sourceSize, localHash, localSize));
        Assert.Equal(expected != null, ImageUtilities.PictureDiffers(sourceHash, sourceSize, localHash, localSize));
    }

    /// <summary>
    /// A file's hash and size come from one read, and the same bytes always hash alike.
    /// True: a local picture is compared with the source's by content.
    /// False: two copies of one picture would read as different.
    /// </summary>
    [Fact]
    public async Task HashFileAsync_File_ReturnsItsHashAndSize()
    {
        var first = Path.Combine(_temp.Root, "a");
        var second = Path.Combine(_temp.Root, "b");
        var other = Path.Combine(_temp.Root, "c");
        await File.WriteAllBytesAsync(first, new byte[] { 1, 2, 3 });
        await File.WriteAllBytesAsync(second, new byte[] { 1, 2, 3 });
        await File.WriteAllBytesAsync(other, new byte[] { 1, 2, 4 });

        var a = await ImageUtilities.HashFileAsync(first, CancellationToken.None);
        var b = await ImageUtilities.HashFileAsync(second, CancellationToken.None);
        var c = await ImageUtilities.HashFileAsync(other, CancellationToken.None);

        Assert.Equal(3, a.Size);
        Assert.NotEmpty(a.Hash);
        Assert.Equal(a, b);
        Assert.NotEqual(a.Hash, c.Hash);
    }

    private static SyncableValue<string> Images(string source, string local)
        => new() { Comparator = new ImageManifestComparator(), Source = source, Local = local };

    private static string Manifest(params (string Type, int Index, long Size)[] entries)
        => JsonSerializer.Serialize(entries
            .GroupBy(e => e.Type)
            .ToDictionary(g => g.Key, g => g.Select(e => new ImageInfoDto { ImageType = e.Type, ImageIndex = e.Index, Size = e.Size, Width = 1, Height = 1, Tag = "t" }).ToList()));

    private static string Tagged(params (string Type, int Index, long Size, string Tag)[] entries)
        => JsonSerializer.Serialize(entries
            .GroupBy(e => e.Type)
            .ToDictionary(g => g.Key, g => g.Select(e => new ImageInfoDto { ImageType = e.Type, ImageIndex = e.Index, Size = e.Size, Tag = e.Tag }).ToList()));

    private static string Paired(params (string Type, int Index, long Size, string SyncedTag)[] entries)
        => JsonSerializer.Serialize(entries
            .GroupBy(e => e.Type)
            .ToDictionary(g => g.Key, g => g.Select(e => new ImageInfoDto { ImageType = e.Type, ImageIndex = e.Index, Size = e.Size, Tag = "mark", SyncedTag = e.SyncedTag }).ToList()));
}
