using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

/// <summary>
/// The image actions download a type's images before they touch the local ones, save each to its slot in source order, and report what did not land.
/// </summary>
public sealed class ImageUtilitiesTests : IDisposable
{
    private static readonly byte[] Picture = { 1, 2, 3, 4 };

    private readonly SyncTempFolder _temp = new("image-apply");
    private readonly Movie _item = new() { Id = Guid.NewGuid(), Name = "Film" };
    private readonly IProviderManager _provider;
    private readonly List<(ImageType Type, int Index, string ContentType, byte[] Bytes)> _saved = new();
    private readonly List<ImageToApply> _asked = new();
    private Func<ImageToApply, (Stream? Stream, string? ContentType)> _source = _ => (new MemoryStream(Picture), "image/png");
    private Exception? _saveFails;

    public ImageUtilitiesTests()
    {
        var (provider, stub) = InterfaceStub.Create<IProviderManager>();
        _provider = provider;
        stub.Answer(nameof(IProviderManager.SaveImage), args =>
        {
            if (_saveFails != null)
            {
                return Task.FromException(_saveFails);
            }

            using var copy = new MemoryStream();
            ((Stream)args[1]!).CopyTo(copy);
            var type = (ImageType)args[3]!;
            _saved.Add((type, (int)args[4]!, (string)args[2]!, copy.ToArray()));
            _item.ImageInfos = _item.ImageInfos.Append(new ItemImageInfo { Type = type, Path = "/saved/" + _saved.Count }).ToArray();
            return Task.CompletedTask;
        });
    }

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A downloaded image is written whole and its size is returned.
    /// True: the user picture apply reads the size it then stores.
    /// False: a size of zero would read as an empty picture and fail the apply.
    /// </summary>
    [Fact]
    public async Task WriteCappedAsync_Stream_WritesTheFileAndReturnsItsSize()
    {
        var path = Path.Combine(_temp.Root, "picture");

        var bytes = await ImageUtilities.WriteCappedAsync(new MemoryStream(Picture), path, CancellationToken.None);

        Assert.Equal(Picture.Length, bytes);
        Assert.Equal(Picture, await File.ReadAllBytesAsync(path));
    }

    /// <summary>
    /// Every image is saved to its own slot under its own type, and the downloads are removed afterward.
    /// True: a set of backdrops lands in order.
    /// False: images would overwrite each other, or temporary files would pile up.
    /// </summary>
    [Fact]
    public async Task ReplaceAsync_EveryDownloadSucceeds_SavesEachToItsSlot()
    {
        var result = await Replace(new ImageToApply("Primary", null, 0), new ImageToApply("Backdrop", 0, 0), new ImageToApply("Backdrop", 1, 1));

        Assert.Equal(new[] { (ImageType.Primary, 0), (ImageType.Backdrop, 0), (ImageType.Backdrop, 1) }, _saved.Select(s => (s.Type, s.Index)));
        Assert.All(_saved, s => Assert.Equal(Picture, s.Bytes));
        Assert.All(_saved, s => Assert.Equal("image/png", s.ContentType));
        Assert.True(result.Changed);
        Assert.Equal((3, 3, 0, 0), (result.Attempted, result.Saved, result.DownloadFailures, result.SaveFailures));
        Assert.Empty(result.Errors);
        Assert.Empty(Directory.EnumerateFiles(_temp.Root, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// A type whose slots arrive out of order is saved in slot order.
    /// True: slot 0 is written before slot 1.
    /// False: a later slot saved first would be appended and then overwritten.
    /// </summary>
    [Fact]
    public async Task ReplaceAsync_SlotsOutOfOrder_SavesInSlotOrder()
    {
        await Replace(new ImageToApply("Backdrop", 7, 1), new ImageToApply("Backdrop", 3, 0));

        Assert.Equal(new[] { 0, 1 }, _saved.Select(s => s.Index));
    }

    /// <summary>
    /// One failed download fails its whole type and leaves that type's local images alone, while the other types still land.
    /// True: a type is never half replaced.
    /// False: the local images would be cleared with nothing to put back.
    /// </summary>
    [Fact]
    public async Task ReplaceAsync_OneDownloadOfATypeFails_KeepsThatTypeAndSavesTheOthers()
    {
        _item.ImageInfos = new[] { new ItemImageInfo { Type = ImageType.Backdrop, Path = "/kept" } };
        _source = image => image is { ImageType: "Backdrop", TargetIndex: 1 } ? (null, null) : (new MemoryStream(Picture), "image/png");

        var result = await Replace(new ImageToApply("Backdrop", 0, 0), new ImageToApply("Backdrop", 1, 1), new ImageToApply("Primary", null, 0));

        Assert.Equal(new[] { ImageType.Primary }, _saved.Select(s => s.Type));
        Assert.Equal("/kept", Assert.Single(_item.GetImages(ImageType.Backdrop)).Path);
        Assert.Equal("Backdrop: download index 1: returned null (kept existing local images)", Assert.Single(result.Errors));
        Assert.Equal((1, 1), (result.Saved, result.DownloadFailures));
        Assert.Empty(Directory.EnumerateFiles(_temp.Root, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// An answer that is not an image, an empty answer, and a download that throws each count as a failed download.
    /// True: an error page or an empty body is never saved as a picture.
    /// False: the local image would be replaced by a file no player can show.
    /// </summary>
    [Theory]
    [InlineData("text/html", 4, "the source answered text/html, which is not an image")]
    [InlineData("image/png", 0, "the source answered 0 bytes")]
    [InlineData("throws", 4, "the line dropped")]
    public async Task ReplaceAsync_AnswerIsNotAUsableImage_FailsTheDownload(string contentType, int length, string reason)
    {
        _source = _ => contentType == "throws" ? throw new IOException("the line dropped") : (new MemoryStream(new byte[length]), contentType);

        var result = await Replace(new ImageToApply("Primary", null, 0));

        Assert.Empty(_saved);
        Assert.False(result.Changed);
        Assert.Equal(1, result.DownloadFailures);
        Assert.Equal("Primary: download index 0: " + reason + " (kept existing local images)", Assert.Single(result.Errors));
        Assert.Empty(Directory.EnumerateFiles(_temp.Root, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// An image with no content type is saved as a JPEG.
    /// True: a source that names no type still has its image saved.
    /// False: the save would be handed no type and refuse the image.
    /// </summary>
    [Fact]
    public async Task ReplaceAsync_NoContentType_SavesAsJpeg()
    {
        _source = _ => (new MemoryStream(Picture), null);

        await Replace(new ImageToApply("Primary", null, 0));

        Assert.Equal("image/jpeg", Assert.Single(_saved).ContentType);
    }

    /// <summary>
    /// An image type Jellyfin does not know is reported and never downloaded, and a type in another letter case is still known.
    /// True: a made up type cannot be saved under a wrong one.
    /// False: the apply would throw and take the known types down with it.
    /// </summary>
    [Fact]
    public async Task ReplaceAsync_UnknownImageType_IsReportedAndSkipped()
    {
        var result = await Replace(new ImageToApply("Hologram", null, 0), new ImageToApply("primary", null, 0));

        Assert.Equal("Hologram: unknown image type", Assert.Single(result.Errors));
        Assert.Equal(new[] { ImageType.Primary }, _saved.Select(s => s.Type));
        Assert.DoesNotContain(_asked, a => a.ImageType == "Hologram");
    }

    /// <summary>
    /// A save that throws is reported for its type and its download is still removed.
    /// True: the row fails and says which image did not save.
    /// False: a failed save would be counted as applied.
    /// </summary>
    [Fact]
    public async Task ReplaceAsync_SaveThrows_ReportsTheSave()
    {
        _saveFails = new IOException("disk full");

        var result = await Replace(new ImageToApply("Primary", null, 0));

        Assert.Equal("Primary: save index 0: disk full", Assert.Single(result.Errors));
        Assert.Equal((0, 1), (result.Saved, result.SaveFailures));
        Assert.Empty(Directory.EnumerateFiles(_temp.Root, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// A cancelled apply throws the cancellation before it downloads anything.
    /// True: a stopped task stops.
    /// False: a stopped task would be reported as failed downloads.
    /// </summary>
    [Fact]
    public async Task ReplaceAsync_Cancelled_Throws()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Replace(cancelled.Token, new ImageToApply("Primary", null, 0)));

        Assert.Empty(_asked);
    }

    /// <summary>
    /// With no work, nothing changes.
    /// True: an item whose source has no images is left alone.
    /// False: the item would be saved for nothing.
    /// </summary>
    [Fact]
    public async Task ReplaceAsync_NoWork_ChangesNothing()
    {
        var result = await Replace();

        Assert.False(result.Changed);
        Assert.Empty(result.Errors);
    }

    private Task<ImageApplyResult> Replace(params ImageToApply[] work) => Replace(CancellationToken.None, work);

    private Task<ImageApplyResult> Replace(CancellationToken cancellationToken, params ImageToApply[] work)
        => ImageUtilities.ReplaceAsync(
            _item,
            work,
            (image, _) =>
            {
                _asked.Add(image);
                return Task.FromResult(_source(image));
            },
            _provider,
            Path.Combine(_temp.Root, "images"),
            NullLogger.Instance,
            "Film",
            cancellationToken);

    /// <summary>
    /// Multiple backdrops keep their source order and get slots 0..n-1.
    /// True: SaveImage writes each backdrop into its own slot, so all of them survive the apply.
    /// False: out-of-order slots make SaveImage append then overwrite, collapsing the set to one image.
    /// </summary>
    [Fact]
    public void AssignSequentialSlots_MultipleBackdrops_NumbersSlotsInSourceOrder()
    {
        var work = new List<(string ImageType, int? ImageIndex)>
        {
            ("Backdrop", 2),
            ("Backdrop", 0),
            ("Backdrop", 1)
        };

        var ordered = ImageUtilities.AssignSequentialSlots(work);

        Assert.Equal(new[] { 0, 1, 2 }, ordered.Select(o => o.TargetIndex));
        Assert.Equal(new int?[] { 0, 1, 2 }, ordered.Select(o => o.SourceIndex));
    }

    /// <summary>
    /// Sparse source indexes are compacted to contiguous slots.
    /// True: the local manifest numbers images by position, so a gap-free target set verifies clean.
    /// False: a slot 5 written into an empty set lands at position 0 and the comparison never settles.
    /// </summary>
    [Fact]
    public void AssignSequentialSlots_SparseSourceIndexes_CompactsToContiguousSlots()
    {
        var work = new List<(string ImageType, int? ImageIndex)>
        {
            ("Backdrop", 5),
            ("Backdrop", 9)
        };

        var ordered = ImageUtilities.AssignSequentialSlots(work);

        Assert.Equal(new[] { 0, 1 }, ordered.Select(o => o.TargetIndex));
        Assert.Equal(new int?[] { 5, 9 }, ordered.Select(o => o.SourceIndex));
    }

    /// <summary>
    /// Null source indexes are treated as zero rather than dropped.
    /// True: a source that omits the index still gets its image downloaded and saved.
    /// False: a null index would sort unpredictably or throw, losing the image entirely.
    /// </summary>
    [Fact]
    public void AssignSequentialSlots_NullSourceIndex_SortsAsZeroAndKeepsTheEntry()
    {
        var work = new List<(string ImageType, int? ImageIndex)>
        {
            ("Backdrop", 1),
            ("Backdrop", null)
        };

        var ordered = ImageUtilities.AssignSequentialSlots(work);

        Assert.Equal(2, ordered.Count);
        Assert.Null(ordered[0].SourceIndex);
        Assert.Equal(0, ordered[0].TargetIndex);
        Assert.Equal(1, ordered[1].SourceIndex);
        Assert.Equal(1, ordered[1].TargetIndex);
    }

    /// <summary>
    /// Slot numbering restarts for each image type.
    /// True: Primary stays at slot 0 while backdrops number independently, matching how Jellyfin stores them.
    /// False: a shared counter would push Primary to slot 1 and orphan it.
    /// </summary>
    [Fact]
    public void AssignSequentialSlots_MixedTypes_NumbersEachTypeIndependently()
    {
        var work = new List<(string ImageType, int? ImageIndex)>
        {
            ("Backdrop", 0),
            ("Primary", 0),
            ("Backdrop", 1)
        };

        var ordered = ImageUtilities.AssignSequentialSlots(work);

        var backdrops = ordered.Where(o => o.ImageType == "Backdrop").ToList();
        var primary = Assert.Single(ordered, o => o.ImageType == "Primary");

        Assert.Equal(new[] { 0, 1 }, backdrops.Select(o => o.TargetIndex));
        Assert.Equal(0, primary.TargetIndex);
    }

    /// <summary>
    /// An empty work list produces an empty result instead of throwing.
    /// True: callers can hand over whatever the source returned without a guard.
    /// False: a throw here would abort an apply that simply had nothing to do.
    /// </summary>
    [Fact]
    public void AssignSequentialSlots_EmptyWork_ReturnsEmpty()
    {
        var ordered = ImageUtilities.AssignSequentialSlots(new List<(string, int?)>());

        Assert.Empty(ordered);
    }
}
