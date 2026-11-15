using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Kiota.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// A content hint is turned away with the right code before anything is downloaded, whenever the file is not this server's to take.
/// </summary>
public sealed class ContentHintHandlerTests : IDisposable
{
    private readonly QueueTestDatabase _db = new();
    private readonly FakeConfigManager _config = QueueSamples.Config();
    private readonly QueuePeerHandler _peer = new();
    private readonly ContentSyncTableManager _table;
    private readonly ContentHintHandler _handler;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentHintHandlerTests"/> class.
    /// </summary>
    public ContentHintHandlerTests()
    {
        _table = new ContentSyncTableManager(_db.Provider, NullLogger<ContentSyncTableManager>.Instance);
        _handler = new ContentHintHandler(new QueueServices(), _config, _table, null!, NullLogger<ContentHintHandler>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose() => _db.Dispose();

    private static InboundHint Hint(string path = "/media/movies/a.mkv", string? key = null, string? itemType = null)
    {
        var hint = QueueSamples.Inbound(HintKind.Content, path, key);
        hint.ItemType = itemType;
        return hint;
    }

    private async Task<HintApplyResult> ApplyAsync(InboundHint hint, SourceServer? origin = null)
    {
        using var client = _peer.Client();
        return await _handler.ApplyAsync(hint, origin ?? QueueSamples.Server(), client, CancellationToken.None);
    }

    /// <summary>
    /// A missing hint, origin, or client is refused.
    /// True: a caller's mistake is named.
    /// False: it surfaces as a null reference partway through an apply.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_NullArgument_Throws()
    {
        using var client = _peer.Client();

        await Assert.ThrowsAsync<ArgumentNullException>(() => _handler.ApplyAsync(null!, QueueSamples.Server(), client, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _handler.ApplyAsync(Hint(), null!, client, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _handler.ApplyAsync(Hint(), QueueSamples.Server(), null!, CancellationToken.None));
    }

    /// <summary>
    /// With Content off the hint is dropped and the origin is not asked.
    /// True: turning Content off stops downloads at once, queued hints included.
    /// False: a hint queued before the switch still brings its file.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_ContentOff_DropsAsModuleOffWithoutAskingTheOrigin()
    {
        _config.Configuration.EnableContentSync = false;

        var result = await ApplyAsync(Hint());

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.ModuleOff, result.Code);
        Assert.Empty(_peer.Requests);
    }

    /// <summary>
    /// A key that is not an item id is dropped as invalid.
    /// True: a malformed row ends at once.
    /// False: it is retried for a day.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_KeyNotAnItemId_DropsAsInvalid()
    {
        var result = await ApplyAsync(Hint(key: "not-an-id"));

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.Invalid, result.Code);
        Assert.Empty(_peer.Requests);
    }

    /// <summary>
    /// A path outside every mapped library is dropped before the origin is asked.
    /// True: this server never reads an item it has no library for.
    /// False: unmapped hints cost a request each and reveal what the origin holds.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_HintPathNotMapped_DropsAsNotMappedWithoutAskingTheOrigin()
    {
        var result = await ApplyAsync(Hint("/elsewhere/a.mkv"));

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.NotMapped, result.Code);
        Assert.Empty(_peer.Requests);
    }

    /// <summary>
    /// An item the origin no longer has is dropped as gone, whether the hint announced a new file or a removal.
    /// True: a file deleted on the origin before the hint applied is not retried.
    /// False: the hint retries for a day against an item that will never come back.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Movie")]
    [InlineData(HintProtocol.ContentRemoved)]
    public async Task ApplyAsync_ItemGoneOnTheOrigin_DropsAsSourceGone(string? itemType)
    {
        _peer.On("/Items", _ => QueuePeerHandler.JsonText(QueueSamples.NoItemsJson));

        var result = await ApplyAsync(Hint(itemType: itemType));

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.SourceGone, result.Code);
    }

    /// <summary>
    /// An item the origin returns without a path is dropped as unreadable.
    /// True: a key that cannot see file paths is named as the cause.
    /// False: the hint retries for a day, or is taken as an item with no file.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_OriginGivesNoPath_DropsAsUnreadable()
    {
        _peer.On("/Items", _ => QueuePeerHandler.JsonText(QueueSamples.ItemsJson(QueueSamples.ItemId, null)));

        var result = await ApplyAsync(Hint());

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.Unreadable, result.Code);
    }

    /// <summary>
    /// A file the mapping's filter leaves out is dropped as filtered, by the path the origin reports.
    /// True: a hint cannot bring a file the next scan would remove.
    /// False: a filtered file is downloaded and then deleted, over and over.
    /// </summary>
    [Theory]
    [InlineData(LibraryFilterMode.Blacklist, "/media/movies/Hidden/a.mkv")]
    [InlineData(LibraryFilterMode.Whitelist, "/media/movies/Other/a.mkv")]
    public async Task ApplyAsync_FileLeftOutByTheFilter_DropsAsFiltered(LibraryFilterMode mode, string originPath)
    {
        var origin = QueueSamples.Server();
        origin.LibraryMappings[0].FilterMode = mode;
        origin.LibraryMappings[0].FilteredItems.Add(new FilteredItem { Path = "/media/movies/Hidden" });
        _peer.On("/Items", _ => QueuePeerHandler.JsonText(QueueSamples.ItemsJson(QueueSamples.ItemId, originPath)));

        var result = await ApplyAsync(Hint(), origin);

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.Filtered, result.Code);
    }

    /// <summary>
    /// A .strm on the origin is dropped as filtered.
    /// True: a pointer to a file the origin does not hold is never copied as media.
    /// False: this server downloads a text file and lists it as a film.
    /// </summary>
    [Theory]
    [InlineData("/media/movies/a.strm")]
    [InlineData("/media/movies/a.STRM")]
    public async Task ApplyAsync_ItemIsAStrmOnTheOrigin_DropsAsFiltered(string originPath)
    {
        _peer.On("/Items", _ => QueuePeerHandler.JsonText(QueueSamples.ItemsJson(QueueSamples.ItemId, originPath)));

        var result = await ApplyAsync(Hint());

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.Filtered, result.Code);
    }

    /// <summary>
    /// A file a higher ranked server already tracks is left to that server.
    /// True: the server order decides who provides a file, for hints as for scans.
    /// False: a lower ranked server's hint replaces the file the first server gave.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_FileTrackedByAHigherRankedServer_DropsAsPriority()
    {
        var first = QueueSamples.Server("first", ServerMode.Pull, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var origin = QueueSamples.Server();
        _config.Configuration.Servers.Add(first);
        _config.Configuration.Servers.Add(origin);
        _table.Upsert(new SyncItem
        {
            SourceItemId = "tracked",
            SourceLibraryId = "lib",
            LocalLibraryId = "local",
            SourcePath = "/media/movies/a.mkv",
            LocalPath = QueueSamples.LocalPath("a.mkv"),
            ServerKey = first.Key,
            Status = SyncStatus.Synced,
            StatusDate = DateTime.UtcNow
        });
        _peer.On("/Items", _ => QueuePeerHandler.JsonText(QueueSamples.ItemsJson(QueueSamples.ItemId, "/media/movies/a.mkv")));

        var result = await ApplyAsync(Hint(), origin);

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.Priority, result.Code);
    }

    /// <summary>
    /// An origin that answers the read with an error fails the apply, and is not read as a missing item.
    /// True: the worker retries, and a server error never looks like a deleted file.
    /// False: one bad answer drops the hint as gone, or settles a link as if its source had left.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_OriginAnswersWithAnError_Throws()
    {
        _peer.OnStatus("/Items", HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<ApiException>(() => ApplyAsync(Hint()));
    }

    /// <summary>
    /// A cancelled apply stops at the read from the origin.
    /// True: a server shutting down ends the pass at once.
    /// False: the hint is dropped or retried on the strength of a cancelled read.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_CancelledToken_Throws()
    {
        using var client = _peer.Client();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _handler.ApplyAsync(Hint(), QueueSamples.Server(), client, cancelled.Token));
    }
}
