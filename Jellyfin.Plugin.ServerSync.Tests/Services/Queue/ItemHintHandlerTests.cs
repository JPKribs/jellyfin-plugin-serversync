using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// A metadata or people hint is dropped, parked, or retried with the right code before any row is written, whenever it cannot apply as it stands.
/// </summary>
public sealed class ItemHintHandlerTests : IDisposable
{
    private readonly QueueTestDatabase _db = new();
    private readonly FakeConfigManager _config = QueueSamples.Config();
    private readonly QueueLocalItems _local = new();
    private readonly QueuePeerHandler _peer = new();
    private readonly ItemHintHandler _handler;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemHintHandlerTests"/> class.
    /// </summary>
    public ItemHintHandlerTests()
    {
        var triage = new HintTriage(_config, _local, new VersionStore(_db.Provider, NullLogger<VersionStore>.Instance));
        _handler = new ItemHintHandler(new QueueServices(), _config, triage, QueueSamples.Resolver(_db.Provider, _config), null!, null!, NullLogger<ItemHintHandler>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose() => _db.Dispose();

    private static InboundHint Metadata(string path = "/media/movies/a.mkv", string? key = null) => QueueSamples.Inbound(HintKind.Metadata, path, key);

    private static InboundHint People(string name = "Jane Doe") => QueueSamples.Inbound(HintKind.People, null, name);

    private async Task<HintApplyResult> ApplyMetadataAsync(InboundHint hint, SourceServer? origin = null, BaseItemDto? prefetched = null)
    {
        using var client = _peer.Client();
        return await _handler.ApplyMetadataAsync(hint, origin ?? QueueSamples.Server(), client, CancellationToken.None, prefetched);
    }

    private async Task<HintApplyResult> ApplyPeopleAsync(InboundHint hint)
    {
        using var client = _peer.Client();
        return await _handler.ApplyPeopleAsync(hint, QueueSamples.Server(), client, CancellationToken.None);
    }

    /// <summary>
    /// A missing hint, origin, or client is refused by both applies.
    /// True: a caller's mistake is named.
    /// False: it surfaces as a null reference partway through an apply.
    /// </summary>
    [Fact]
    public async Task Apply_NullArgument_Throws()
    {
        using var client = _peer.Client();
        var origin = QueueSamples.Server();

        await Assert.ThrowsAsync<ArgumentNullException>(() => _handler.ApplyMetadataAsync(null!, origin, client, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _handler.ApplyMetadataAsync(Metadata(), null!, client, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _handler.ApplyMetadataAsync(Metadata(), origin, null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _handler.ApplyPeopleAsync(null!, origin, client, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _handler.ApplyPeopleAsync(People(), null!, client, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _handler.ApplyPeopleAsync(People(), origin, null!, CancellationToken.None));
    }

    /// <summary>
    /// With Metadata off the hint is dropped and the origin is not asked.
    /// True: turning Metadata off stops edits arriving at once.
    /// False: a hint queued before the switch still overwrites local metadata.
    /// </summary>
    [Fact]
    public async Task ApplyMetadataAsync_MetadataOff_DropsAsModuleOff()
    {
        _config.Configuration.EnableMetadataSync = false;

        var result = await ApplyMetadataAsync(Metadata());

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
    public async Task ApplyMetadataAsync_KeyNotAnItemId_DropsAsInvalid()
    {
        var result = await ApplyMetadataAsync(Metadata(key: "not-an-id"));

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.Invalid, result.Code);
    }

    /// <summary>
    /// A path outside every mapped library is dropped before the origin is asked.
    /// True: this server never reads an item it has no library for.
    /// False: unmapped hints cost a request each.
    /// </summary>
    [Fact]
    public async Task ApplyMetadataAsync_HintPathNotMapped_DropsAsNotMappedWithoutAskingTheOrigin()
    {
        var result = await ApplyMetadataAsync(Metadata("/elsewhere/a.mkv"));

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.NotMapped, result.Code);
        Assert.Empty(_peer.Requests);
    }

    /// <summary>
    /// A hint whose local item has not arrived is parked under the item's local path, and the origin is not asked.
    /// True: metadata for a file still downloading applies when the file lands.
    /// False: it is dropped, and the new item keeps stale metadata until a scan.
    /// </summary>
    [Fact]
    public async Task ApplyMetadataAsync_LocalItemMissing_ParksUnderItsLocalPath()
    {
        var result = await ApplyMetadataAsync(Metadata());

        Assert.Equal(HintApplyOutcome.Parked, result.Outcome);
        Assert.Equal(HintTriage.PathParkKey(QueueSamples.LocalPath("a.mkv")), result.ParkKey);
        Assert.Empty(_peer.Requests);
    }

    /// <summary>
    /// An origin that answers with an error, or cannot be reached, is retried later.
    /// True: a restart of the origin costs a retry, not the change.
    /// False: the hint is dropped as if the item were gone.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplyMetadataAsync_OriginFailsTheRead_RetriesLater(bool reachable)
    {
        _local.AddItem(QueueSamples.LocalPath("a.mkv"));
        if (reachable)
        {
            _peer.OnStatus("/Items", HttpStatusCode.InternalServerError);
        }
        else
        {
            _peer.OnUnreachable("/Items");
        }

        var result = await ApplyMetadataAsync(Metadata());

        Assert.Equal(HintApplyOutcome.Retry, result.Outcome);
        Assert.Equal(HintCode.Queued, result.Code);
        Assert.NotNull(result.Reason);
    }

    /// <summary>
    /// An item the origin no longer has is dropped as gone.
    /// True: an edit to an item deleted since is not retried.
    /// False: the hint retries for a day against nothing.
    /// </summary>
    [Fact]
    public async Task ApplyMetadataAsync_ItemGoneOnTheOrigin_DropsAsSourceGone()
    {
        _local.AddItem(QueueSamples.LocalPath("a.mkv"));
        _peer.On("/Items", _ => QueuePeerHandler.JsonText(QueueSamples.NoItemsJson));

        var result = await ApplyMetadataAsync(Metadata());

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.SourceGone, result.Code);
    }

    /// <summary>
    /// A prefetched item is used as read, so the origin is not asked again, and one without an id counts as gone.
    /// True: a pass of many hints costs one page, and a half empty answer is not applied.
    /// False: every hint reads its own item, or an item with no id is written.
    /// </summary>
    [Fact]
    public async Task ApplyMetadataAsync_PrefetchedItemWithoutAnId_DropsAsSourceGoneWithoutAskingTheOrigin()
    {
        _local.AddItem(QueueSamples.LocalPath("a.mkv"));

        var result = await ApplyMetadataAsync(Metadata(), prefetched: new BaseItemDto { Path = "/media/movies/a.mkv" });

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.SourceGone, result.Code);
        Assert.Empty(_peer.Requests);
    }

    /// <summary>
    /// An item the origin reports inside a filtered folder is dropped as filtered, whatever path the hint claimed.
    /// True: the filter is applied to where the item really is.
    /// False: a hint that names an allowed path writes metadata for a filtered item.
    /// </summary>
    [Fact]
    public async Task ApplyMetadataAsync_OriginPathLeftOutByTheFilter_DropsAsFiltered()
    {
        var origin = QueueSamples.Server();
        origin.LibraryMappings[0].FilterMode = LibraryFilterMode.Blacklist;
        origin.LibraryMappings[0].FilteredItems.Add(new FilteredItem { Path = "/media/movies/Hidden" });
        _local.AddItem(QueueSamples.LocalPath("a.mkv"));

        var result = await ApplyMetadataAsync(Metadata(), origin, new BaseItemDto { Id = QueueSamples.ItemId, Path = "/media/movies/Hidden/a.mkv" });

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.Filtered, result.Code);
    }

    /// <summary>
    /// A cancelled apply stops at the read from the origin.
    /// True: a server shutting down ends the pass at once.
    /// False: the cancellation is counted as a failed attempt against the hint.
    /// </summary>
    [Fact]
    public async Task ApplyMetadataAsync_CancelledToken_Throws()
    {
        _local.AddItem(QueueSamples.LocalPath("a.mkv"));
        using var client = _peer.Client();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _handler.ApplyMetadataAsync(Metadata(), QueueSamples.Server(), client, cancelled.Token));
    }

    /// <summary>
    /// With People off the hint is dropped and the origin is not asked.
    /// True: turning People off stops edits to people at once.
    /// False: a hint queued before the switch still overwrites a person.
    /// </summary>
    [Fact]
    public async Task ApplyPeopleAsync_PeopleOff_DropsAsModuleOff()
    {
        _config.Configuration.EnablePeopleSync = false;

        var result = await ApplyPeopleAsync(People());

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.ModuleOff, result.Code);
        Assert.Empty(_peer.Requests);
    }

    /// <summary>
    /// A people hint with no name is dropped as invalid.
    /// True: a nameless row ends at once.
    /// False: the origin is asked for a person with a blank name.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ApplyPeopleAsync_NoName_DropsAsInvalid(string name)
    {
        var result = await ApplyPeopleAsync(People(name));

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.Invalid, result.Code);
        Assert.Empty(_peer.Requests);
    }

    /// <summary>
    /// A hint for a person who is not here yet is parked under the person's key.
    /// True: an edit to a person applies once an item that credits them arrives.
    /// False: the edit is lost and the person appears here with no biography or image.
    /// </summary>
    [Fact]
    public async Task ApplyPeopleAsync_PersonMissing_ParksUnderThePersonsKey()
    {
        var result = await ApplyPeopleAsync(People("Jane Doe"));

        Assert.Equal(HintApplyOutcome.Parked, result.Outcome);
        Assert.Equal(HintTriage.PersonParkKey("Jane Doe"), result.ParkKey);
        Assert.Empty(_peer.Requests);
    }

    /// <summary>
    /// An origin that answers the person read with an error is retried later.
    /// True: a failing origin costs a retry, not the change.
    /// False: the hint is dropped as if the person were gone.
    /// </summary>
    [Fact]
    public async Task ApplyPeopleAsync_OriginAnswersWithAnError_RetriesLater()
    {
        _local.AddPerson("Jane Doe");
        _peer.OnStatus("/Persons/", HttpStatusCode.InternalServerError);

        var result = await ApplyPeopleAsync(People("Jane Doe"));

        Assert.Equal(HintApplyOutcome.Retry, result.Outcome);
        Assert.NotNull(result.Reason);
    }

    /// <summary>
    /// A person the origin does not have, or returns without a name, is dropped as gone.
    /// True: an edit to a person removed since is not retried.
    /// False: the hint retries for a day against nothing.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.OK, "{}")]
    public async Task ApplyPeopleAsync_PersonGoneOnTheOrigin_DropsAsSourceGone(HttpStatusCode status, string body)
    {
        _local.AddPerson("Jane Doe");
        _peer.On("/Persons/", _ => new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });

        var result = await ApplyPeopleAsync(People("Jane Doe"));

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.SourceGone, result.Code);
    }
}
