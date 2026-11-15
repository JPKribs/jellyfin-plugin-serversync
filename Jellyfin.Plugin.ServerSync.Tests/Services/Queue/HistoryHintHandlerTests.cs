using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Kiota.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// A history hint is dropped, parked, or retried with the right code before any user data is read or written, whenever it cannot apply as it stands.
/// </summary>
public sealed class HistoryHintHandlerTests : IDisposable
{
    private readonly QueueTestDatabase _db = new();
    private readonly FakeConfigManager _config = QueueSamples.Config();
    private readonly QueueLocalItems _local = new();
    private readonly QueuePeerHandler _peer = new();
    private readonly HistoryHintHandler _handler;

    /// <summary>
    /// Initializes a new instance of the <see cref="HistoryHintHandlerTests"/> class.
    /// </summary>
    public HistoryHintHandlerTests()
    {
        var triage = new HintTriage(_config, _local, new VersionStore(_db.Provider, NullLogger<VersionStore>.Instance));
        _handler = new HistoryHintHandler(null!, null!, null!, null!, null!, null!, null!, _config, triage, null!, NullLogger<HistoryHintHandler>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose() => _db.Dispose();

    private static InboundHint Hint(string path = "/media/movies/a.mkv", string? key = null)
        => QueueSamples.Inbound(HintKind.History, path, key ?? HintProtocol.HistoryKey(QueueSamples.UserId, QueueSamples.ItemId));

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
    /// With History off the hint is dropped and the origin is not asked.
    /// True: turning History off stops plays arriving at once.
    /// False: a hint queued before the switch still marks an item played.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_HistoryOff_DropsAsModuleOff()
    {
        _config.Configuration.EnableHistorySync = false;

        var result = await ApplyAsync(Hint());

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.ModuleOff, result.Code);
        Assert.Empty(_peer.Requests);
    }

    /// <summary>
    /// A key that is not a user id and an item id is dropped as invalid.
    /// True: a malformed row ends at once.
    /// False: half a key is matched against the wrong user.
    /// </summary>
    [Theory]
    [InlineData("not-a-key")]
    [InlineData("22222222222222222222222222222222|not-an-id")]
    public async Task ApplyAsync_KeyMalformed_DropsAsInvalid(string key)
    {
        var result = await ApplyAsync(Hint(key: key));

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.Invalid, result.Code);
    }

    /// <summary>
    /// A user with no mapping, or a path outside every mapped library, is dropped as not mapped before the origin is asked.
    /// True: history is never read for a user or a library this server does not sync.
    /// False: unmapped plays cost a request each and are retried.
    /// </summary>
    [Theory]
    [InlineData(false, "/media/movies/a.mkv")]
    [InlineData(true, "/elsewhere/a.mkv")]
    public async Task ApplyAsync_UserOrPathNotMapped_DropsAsNotMappedWithoutAskingTheOrigin(bool userMapped, string path)
    {
        var user = userMapped ? QueueSamples.UserId : Guid.Parse("99999999999999999999999999999999");

        var result = await ApplyAsync(Hint(path, HintProtocol.HistoryKey(user, QueueSamples.ItemId)));

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.NotMapped, result.Code);
        Assert.Empty(_peer.Requests);
    }

    /// <summary>
    /// A hint whose local item has not arrived is parked under the item's local path.
    /// True: a play of a file still downloading applies when the file lands.
    /// False: the play is lost until the next scheduled sync.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_LocalItemMissing_ParksUnderItsLocalPath()
    {
        var result = await ApplyAsync(Hint());

        Assert.Equal(HintApplyOutcome.Parked, result.Outcome);
        Assert.Equal(HintTriage.PathParkKey(QueueSamples.LocalPath("a.mkv")), result.ParkKey);
        Assert.Empty(_peer.Requests);
    }

    /// <summary>
    /// An origin that cannot be reached is retried later.
    /// True: a peer that is down costs a retry, not the play.
    /// False: the hint is dropped as if the item were gone.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_OriginUnreachable_RetriesLater()
    {
        _local.AddItem(QueueSamples.LocalPath("a.mkv"));
        _peer.OnUnreachable("/Items");

        var result = await ApplyAsync(Hint());

        Assert.Equal(HintApplyOutcome.Retry, result.Outcome);
        Assert.Equal(HintCode.Queued, result.Code);
    }

    /// <summary>
    /// An origin that answers the read with an error fails the apply, and is not read as a missing item.
    /// True: the worker retries, and a server error never looks like a deleted item.
    /// False: one bad answer drops the play for good.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_OriginAnswersWithAnError_Throws()
    {
        _local.AddItem(QueueSamples.LocalPath("a.mkv"));
        _peer.OnStatus("/Items", HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<ApiException>(() => ApplyAsync(Hint()));
    }

    /// <summary>
    /// An item the origin no longer has is dropped as gone.
    /// True: a play of an item deleted since is not retried.
    /// False: the hint retries for a day against nothing.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_ItemGoneOnTheOrigin_DropsAsSourceGone()
    {
        _local.AddItem(QueueSamples.LocalPath("a.mkv"));
        _peer.On("/Items", _ => QueuePeerHandler.JsonText(QueueSamples.NoItemsJson));

        var result = await ApplyAsync(Hint());

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.SourceGone, result.Code);
    }

    /// <summary>
    /// An item the origin reports inside a filtered folder is dropped as filtered, whatever path the hint claimed.
    /// True: history follows the filter by where the item really is.
    /// False: a hint that names an allowed path writes history for a filtered item.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_OriginPathLeftOutByTheFilter_DropsAsFiltered()
    {
        var origin = QueueSamples.Server();
        origin.LibraryMappings[0].FilterMode = LibraryFilterMode.Blacklist;
        origin.LibraryMappings[0].FilteredItems.Add(new FilteredItem { Path = "/media/movies/Hidden" });
        _local.AddItem(QueueSamples.LocalPath("a.mkv"));
        _peer.On("/Items", _ => QueuePeerHandler.JsonText(QueueSamples.ItemsJson(QueueSamples.ItemId, "/media/movies/Hidden/a.mkv")));

        var result = await ApplyAsync(Hint(), origin);

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.Filtered, result.Code);
    }

    /// <summary>
    /// The origin is asked for the mapped user's own data for the hinted item.
    /// True: the merge works from that user's history on the origin.
    /// False: another user's plays, or no user data at all, are merged into this user's history.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_ReadsTheItem_AsksForTheOriginUsersData()
    {
        _local.AddItem(QueueSamples.LocalPath("a.mkv"));
        _peer.On("/Items", _ => QueuePeerHandler.JsonText(QueueSamples.NoItemsJson));

        await ApplyAsync(Hint());

        var request = Assert.Single(_peer.Requests);
        Assert.Contains(QueueSamples.UserId.ToString("D"), request.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(QueueSamples.ItemId.ToString("D"), request.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("enableUserData=true", request.Query, StringComparison.OrdinalIgnoreCase);
    }
}
