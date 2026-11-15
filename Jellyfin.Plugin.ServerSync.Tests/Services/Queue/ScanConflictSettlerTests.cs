using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Tasks.MetadataSync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// A scan's queued rows keep a newer edit made here, take a newer edit made there, and are held when the peer's stamps cannot be read.
/// </summary>
public sealed class ScanConflictSettlerTests : IDisposable
{
    private const string ThisServer = "this-server";
    private const string PluginVersion = "1.2.3.4";
    private static readonly DateTime Earlier = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 10, 1, 13, 0, 0, DateTimeKind.Utc);
    private static readonly Guid LocalItem = Guid.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly string FirstItem = Guid.Parse("cccccccccccccccccccccccccccccccc").ToString("N");
    private static readonly string SecondItem = Guid.Parse("dddddddddddddddddddddddddddddddd").ToString("N");

    private readonly QueueTestDatabase _db = new();
    private readonly FakeConfigManager _config = new() { PluginVersion = PluginVersion };
    private readonly VersionStore _versions;
    private readonly OutboundHintStore _outbound;
    private readonly ScanConflictSettler _settler;
    private readonly List<MetadataSyncItem> _stored = new();
    private readonly List<ScanSource> _sources = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ScanConflictSettlerTests"/> class.
    /// </summary>
    public ScanConflictSettlerTests()
    {
        QueueSamples.Enable(_config.Configuration);
        _versions = new VersionStore(_db.Provider, NullLogger<VersionStore>.Instance);
        _outbound = new OutboundHintStore(_db.Provider, NullLogger<OutboundHintStore>.Instance);
        var host = QueueSamples.Host(ThisServer);
        var resolver = new VersionConflictResolver(_versions, host, _config, NullLogger<VersionConflictResolver>.Instance);
        var worker = new OutboundHintWorker(_outbound, _config, null!, null!, null!, new HintSuppression(), null!, NullLogger<OutboundHintWorker>.Instance);
        var publisher = new LocalHintPublisher(_config, _outbound, _versions, worker, host, NullLogger<LocalHintPublisher>.Instance);
        _settler = new ScanConflictSettler(resolver, publisher);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var source in _sources)
        {
            source.Dispose();
        }

        _db.Dispose();
    }

    private static string LocalKey => HintProtocol.MetadataKey(LocalItem);

    private static MetadataSyncItem Row(string serverKey = "peer", string peerKey = "peer-item", Guid? localItem = null, bool withLocalItem = true) => new()
    {
        SourceLibraryId = "lib",
        LocalLibraryId = "local",
        SourceItemId = peerKey,
        LocalItemId = withLocalItem ? (localItem ?? LocalItem).ToString("N") : null,
        ItemName = "Item",
        LocalPath = QueueSamples.LocalPath("a.mkv"),
        ServerKey = serverKey,
        Status = SyncStatus.Queued
    };

    private ScanSource Source(QueuePeerHandler peer, string key = "peer", ServerMode mode = ServerMode.Sync, bool configured = true)
    {
        var server = QueueSamples.Server(key, mode, key + "-id");
        if (configured)
        {
            _config.Configuration.Servers.Add(server);
        }

        var source = new ScanSource(server, peer.Client(server.Url), _sources.Count);
        _sources.Add(source);
        return source;
    }

    private static QueuePeerHandler PeerWithStamps(params ObjectVersion[] held)
        => new QueuePeerHandler()
            .OnJson("/Sync/Peer/Capabilities", new PeerCapabilities { ServerId = "peer-id", PluginVersion = PluginVersion })
            .OnJson("/Sync/Peer/Versions", new VersionsResponse { Items = new List<ObjectVersion>(held) });

    private static ObjectVersion Stamp(string server, DateTime at, string key = "peer-item")
        => new() { Kind = HintKind.Metadata, Key = key, ServerId = server, Timestamp = at };

    private void HeldHere(string server, DateTime at)
        => _versions.Set(new ObjectVersion { Kind = HintKind.Metadata, Key = LocalKey, ServerId = server, Timestamp = at });

    private Task SettleAsync(IList<MetadataSyncItem> queued, IReadOnlyList<ScanSource> sources, CancellationToken cancellationToken = default)
        => _settler.SettleAsync(
            queued,
            sources,
            HintKind.Metadata,
            row => Guid.TryParse(row.LocalItemId, out var id) ? HintProtocol.MetadataKey(id) : string.Empty,
            row => row.SourceItemId,
            row => (row.LocalItemId, row.LocalPath),
            _stored.Add,
            NullLogger.Instance,
            cancellationToken);

    /// <summary>
    /// A missing collaborator or argument is refused.
    /// True: a wiring mistake fails at startup and a bad call is named.
    /// False: a scan ends in a null reference halfway through its rows.
    /// </summary>
    [Fact]
    public async Task ConstructorAndSettle_NullArgument_Throw()
    {
        var host = QueueSamples.Host(ThisServer);
        var resolver = new VersionConflictResolver(_versions, host, _config, NullLogger<VersionConflictResolver>.Instance);
        var publisher = new LocalHintPublisher(_config, _outbound, _versions, null!, host, NullLogger<LocalHintPublisher>.Instance);
        var sources = Array.Empty<ScanSource>();
        var rows = new List<MetadataSyncItem>();

        Assert.Throws<ArgumentNullException>(() => new ScanConflictSettler(null!, publisher));
        Assert.Throws<ArgumentNullException>(() => new ScanConflictSettler(resolver, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => SettleAsync(null!, sources));
        await Assert.ThrowsAsync<ArgumentNullException>(() => SettleAsync(rows, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _settler.SettleAsync(rows, sources, HintKind.Metadata, null!, r => r.SourceItemId, r => (r.LocalItemId, r.LocalPath), _stored.Add, NullLogger.Instance, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _settler.SettleAsync(rows, sources, HintKind.Metadata, r => r.SourceItemId, r => r.SourceItemId, r => (r.LocalItemId, r.LocalPath), null!, NullLogger.Instance, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _settler.ServersWithVersionsAsync(null!, CancellationToken.None));
    }

    /// <summary>
    /// A source with no queued rows is not asked anything.
    /// True: a scan that queued nothing costs no peer request.
    /// False: every scan asks every server for its capabilities whether or not there is anything to decide.
    /// </summary>
    [Fact]
    public async Task SettleAsync_NoRowsForTheSource_DoesNotAskIt()
    {
        var peer = PeerWithStamps();
        var source = Source(peer);

        await SettleAsync(new List<MetadataSyncItem> { Row(serverKey: "another-server") }, new[] { source });

        Assert.Empty(peer.Requests);
        Assert.Empty(_stored);
    }

    /// <summary>
    /// Rows from a source that keeps no stamps stay queued, even over an edit recorded here.
    /// True: a plain Jellyfin source is the source of truth, as it always was.
    /// False: a local edit blocks a Pull source forever, since it can never answer with a stamp.
    /// </summary>
    [Fact]
    public async Task SettleAsync_SourceKeepsNoStamps_LeavesRowsQueued()
    {
        HeldHere(ThisServer, Later);
        var peer = new QueuePeerHandler();
        var row = Row();

        await SettleAsync(new List<MetadataSyncItem> { row }, new[] { Source(peer) });

        Assert.Equal(SyncStatus.Queued, row.Status);
        Assert.Empty(_stored);
        Assert.Equal(0, peer.CountOf("/Sync/Peer/Versions"));
    }

    /// <summary>
    /// When a peer's stamps cannot be read, a row whose local value carries an edit is held and one that carries none stays queued.
    /// True: an edit made here is not overwritten on the strength of a failed request.
    /// False: one bad answer from a peer undoes edits made on this server.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SettleAsync_StampsCannotBeRead_HoldsOnlyRowsEditedHere(bool capabilitiesFail)
    {
        HeldHere(ThisServer, Later);
        var peer = capabilitiesFail
            ? new QueuePeerHandler().OnStatus("/Sync/Peer/Capabilities", HttpStatusCode.BadGateway)
            : PeerWithStamps().OnStatus("/Sync/Peer/Versions", HttpStatusCode.InternalServerError);
        var edited = Row();
        var untouched = Row(peerKey: "other-item", localItem: Guid.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));

        await SettleAsync(new List<MetadataSyncItem> { edited, untouched }, new[] { Source(peer) });

        Assert.Equal(SyncStatus.Synced, edited.Status);
        Assert.Same(edited, Assert.Single(_stored));
        Assert.Equal(SyncStatus.Queued, untouched.Status);
    }

    /// <summary>
    /// A newer edit made on this server is kept, stored, and announced to the peer.
    /// True: the scan leaves the local value and the peer is told to take it.
    /// False: the scan overwrites the newer local edit with the peer's older one.
    /// </summary>
    [Fact]
    public async Task SettleAsync_EditHereIsNewer_KeepsItAndAnnouncesIt()
    {
        HeldHere(ThisServer, Later);
        var row = Row();

        await SettleAsync(new List<MetadataSyncItem> { row }, new[] { Source(PeerWithStamps(Stamp("peer-id", Earlier))) });

        Assert.Equal(SyncStatus.Synced, row.Status);
        Assert.NotNull(row.Reason);
        Assert.Same(row, Assert.Single(_stored));
        var announced = Assert.Single(_outbound.GetAll());
        Assert.Equal("peer", announced.PeerKey);
        Assert.Equal(LocalKey, announced.Key);
        Assert.Equal(Later, announced.VersionTimestamp);
    }

    /// <summary>
    /// A kept value that this server took from a third server is not announced from here.
    /// True: each edit is announced by the server that made it.
    /// False: this server republishes another server's edit under that server's stamp.
    /// </summary>
    [Fact]
    public async Task SettleAsync_KeptValueCameFromAThirdServer_IsNotAnnounced()
    {
        HeldHere("third-server", Later);
        var row = Row();

        await SettleAsync(new List<MetadataSyncItem> { row }, new[] { Source(PeerWithStamps(Stamp("peer-id", Earlier))) });

        Assert.Equal(SyncStatus.Synced, row.Status);
        Assert.Empty(_outbound.GetAll());
    }

    /// <summary>
    /// A kept value is not announced to a source this server only pulls from.
    /// True: nothing is ever queued for a server that does not take changes.
    /// False: rows pile up for a Pull source and are dropped on the next delivery pass.
    /// </summary>
    [Fact]
    public async Task SettleAsync_SourceOnlyPulledFrom_KeepsWithoutAnnouncing()
    {
        HeldHere(ThisServer, Later);
        var row = Row();

        await SettleAsync(new List<MetadataSyncItem> { row }, new[] { Source(PeerWithStamps(Stamp("peer-id", Earlier)), mode: ServerMode.Pull) });

        Assert.Equal(SyncStatus.Synced, row.Status);
        Assert.Empty(_outbound.GetAll());
    }

    /// <summary>
    /// A newer edit on the peer leaves the row queued and keeps the peer's stamp for when the value applies.
    /// True: the row applies, and the stamp recorded afterwards is the one read with the value.
    /// False: the newer peer edit is discarded, or applied with no stamp to defend it.
    /// </summary>
    [Fact]
    public async Task SettleAsync_EditThereIsNewer_LeavesTheRowQueuedWithThePeersStampPending()
    {
        HeldHere(ThisServer, Earlier);
        var row = Row();

        await SettleAsync(new List<MetadataSyncItem> { row }, new[] { Source(PeerWithStamps(Stamp("peer-id", Later))) });

        Assert.Equal(SyncStatus.Queued, row.Status);
        Assert.Empty(_stored);
        Assert.True(_versions.PromotePending(HintKind.Metadata, LocalKey));
        Assert.Equal("peer-id", _versions.Get(HintKind.Metadata, LocalKey)?.ServerId);
    }

    /// <summary>
    /// With no stamp on either side the row stays queued and any stale pending stamp is forgotten.
    /// True: an unstamped value applies as before and leaves no stamp behind.
    /// False: a pending stamp from an earlier scan is recorded for a value it never belonged to.
    /// </summary>
    [Fact]
    public async Task SettleAsync_NoStampOnEitherSide_LeavesTheRowQueuedAndClearsPending()
    {
        _versions.SetPending(new ObjectVersion { Kind = HintKind.Metadata, Key = LocalKey, ServerId = "stale", Timestamp = Earlier });
        var row = Row();

        await SettleAsync(new List<MetadataSyncItem> { row }, new[] { Source(PeerWithStamps()) });

        Assert.Equal(SyncStatus.Queued, row.Status);
        Assert.False(_versions.PromotePending(HintKind.Metadata, LocalKey));
    }

    /// <summary>
    /// A row with no local item is left alone.
    /// True: a row that matched nothing here is neither kept nor announced.
    /// False: the settler throws on a missing id and stops the scan.
    /// </summary>
    [Fact]
    public async Task SettleAsync_RowWithoutALocalItem_IsSkipped()
    {
        var row = Row(withLocalItem: false);

        await SettleAsync(new List<MetadataSyncItem> { row }, new[] { Source(PeerWithStamps(Stamp("peer-id", Earlier))) });

        Assert.Equal(SyncStatus.Queued, row.Status);
        Assert.Empty(_stored);
    }

    /// <summary>
    /// A cancelled settle stops and says so.
    /// True: a scan that is being stopped ends at once with its rows untouched.
    /// False: the cancellation is read as an unreadable peer and edited rows are held.
    /// </summary>
    [Fact]
    public async Task SettleAsync_CancelledToken_Throws()
    {
        HeldHere(ThisServer, Later);
        var row = Row();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SettleAsync(new List<MetadataSyncItem> { row }, new[] { Source(PeerWithStamps()) }, cancelled.Token));

        Assert.Equal(SyncStatus.Queued, row.Status);
    }

    /// <summary>
    /// Only the servers that answer as keeping stamps are listed.
    /// True: offers are weighed by edit time only between servers that can say when they were edited.
    /// False: a plain Jellyfin source, or one that cannot be read, is weighed as if it held stamps.
    /// </summary>
    [Fact]
    public async Task ServersWithVersionsAsync_MixedSources_ListsOnlyThoseThatKeepStamps()
    {
        var sources = new[]
        {
            Source(PeerWithStamps(), "keeps"),
            Source(new QueuePeerHandler(), "plain"),
            Source(new QueuePeerHandler().OnStatus("/Sync/Peer/Capabilities", HttpStatusCode.BadGateway), "failing")
        };

        var versioned = await _settler.ServersWithVersionsAsync(sources, CancellationToken.None);

        Assert.Equal(new[] { "keeps" }, versioned.ToArray());
        Assert.Contains("KEEPS", versioned);
    }

    /// <summary>
    /// Between servers that keep stamps, the one with the newest edit of a thing wins over rank.
    /// True: a film edited last on the second server takes that server's metadata.
    /// False: the first server always wins, however old its copy.
    /// </summary>
    [Fact]
    public async Task NewestOffersAsync_LowerRankedServerEditedLast_WinsOverRank()
    {
        var first = Source(PeerWithStamps(Stamp("first-id", Earlier, FirstItem)), "first");
        var second = Source(PeerWithStamps(Stamp("second-id", Later, SecondItem)), "second");

        var winners = await NewestAsync(new[] { Offer(first, FirstItem), Offer(second, SecondItem) }, "first", "second");

        Assert.Same(second, Assert.Single(winners).Value.Source);
    }

    /// <summary>
    /// Rank decides when the top server edited last, the stamps are the same edit, or the lower server has no stamp.
    /// True: the configured order stands whenever the stamps do not say otherwise.
    /// False: a tie or a missing stamp flips the winner away from the first server.
    /// </summary>
    [Theory]
    [InlineData("top-newer")]
    [InlineData("same-edit")]
    [InlineData("lower-unstamped")]
    public async Task NewestOffersAsync_StampsDoNotFavorTheLowerServer_RankDecides(string scenario)
    {
        var firstStamp = scenario == "top-newer" ? Stamp("first-id", Later, FirstItem) : Stamp("shared-id", Earlier, FirstItem);
        var secondHeld = scenario switch
        {
            "top-newer" => new[] { Stamp("second-id", Earlier, SecondItem) },
            "same-edit" => new[] { Stamp("shared-id", Earlier, SecondItem) },
            _ => Array.Empty<ObjectVersion>()
        };
        var first = Source(PeerWithStamps(firstStamp), "first");
        var second = Source(PeerWithStamps(secondHeld), "second");

        var winners = await NewestAsync(new[] { Offer(first, FirstItem), Offer(second, SecondItem) }, "first", "second");

        Assert.Empty(winners);
    }

    /// <summary>
    /// A lower server with a stamp wins over a top server that has none for the thing.
    /// True: an edit recorded anywhere beats a value nobody is known to have edited.
    /// False: an unstamped copy on the first server hides a real edit on the second.
    /// </summary>
    [Fact]
    public async Task NewestOffersAsync_TopServerHasNoStampForIt_LowerStampWins()
    {
        var first = Source(PeerWithStamps(), "first");
        var second = Source(PeerWithStamps(Stamp("second-id", Earlier, SecondItem)), "second");

        var winners = await NewestAsync(new[] { Offer(first, FirstItem), Offer(second, SecondItem) }, "first", "second");

        Assert.Same(second, Assert.Single(winners).Value.Source);
    }

    /// <summary>
    /// When the top server keeps no stamps, or only one offer can be weighed, rank decides and no server is asked.
    /// True: a plain Jellyfin source at the top stays the source of truth.
    /// False: a lower server with stamps overrides a first server that cannot answer.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task NewestOffersAsync_NothingToWeigh_RankDecidesWithoutAsking(bool firstVersioned, bool secondVersioned)
    {
        var firstPeer = PeerWithStamps(Stamp("first-id", Earlier, FirstItem));
        var secondPeer = PeerWithStamps(Stamp("second-id", Later, SecondItem));
        var first = Source(firstPeer, "first");
        var second = Source(secondPeer, "second");
        var versioned = new List<string>();
        if (firstVersioned)
        {
            versioned.Add("first");
        }

        if (secondVersioned)
        {
            versioned.Add("second");
        }

        var winners = await NewestAsync(new[] { Offer(first, FirstItem), Offer(second, SecondItem) }, versioned.ToArray());

        Assert.Empty(winners);
        Assert.Empty(firstPeer.Requests);
        Assert.Empty(secondPeer.Requests);
    }

    /// <summary>
    /// When one server's stamps cannot be read, rank decides for the things it offers.
    /// True: a failed request never hands the win to whichever server did answer.
    /// False: a server that is briefly down loses every contested item to a lower one.
    /// </summary>
    [Fact]
    public async Task NewestOffersAsync_OneServerCannotBeRead_RankDecides()
    {
        var first = Source(new QueuePeerHandler().OnStatus("/Sync/Peer/Versions", HttpStatusCode.InternalServerError), "first");
        var second = Source(PeerWithStamps(Stamp("second-id", Later, SecondItem)), "second");

        var winners = await NewestAsync(new[] { Offer(first, FirstItem), Offer(second, SecondItem) }, "first", "second");

        Assert.Empty(winners);
    }

    /// <summary>
    /// Nothing contested gives no winners, and a missing argument is refused.
    /// True: a scan with one source per item costs nothing here.
    /// False: an empty input throws and stops the scan.
    /// </summary>
    [Fact]
    public async Task NewestOffersAsync_NothingContestedOrNullArgument_GivesNothingOrThrows()
    {
        var empty = new Dictionary<string, IReadOnlyList<MetadataWork>>();
        var versioned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var winners = await _settler.NewestOffersAsync(empty, HintKind.Metadata, versioned, o => o.Source, PeerKeyOf, CancellationToken.None);

        Assert.Empty(winners);
        await Assert.ThrowsAsync<ArgumentNullException>(() => _settler.NewestOffersAsync<MetadataWork>(null!, HintKind.Metadata, versioned, o => o.Source, PeerKeyOf, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _settler.NewestOffersAsync(empty, HintKind.Metadata, null!, o => o.Source, PeerKeyOf, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _settler.NewestOffersAsync(empty, HintKind.Metadata, versioned, null!, PeerKeyOf, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _settler.NewestOffersAsync(empty, HintKind.Metadata, versioned, o => o.Source, null!, CancellationToken.None));
    }

    private static MetadataWork Offer(ScanSource source, string peerItem)
        => new(source, source.Server.LibraryMappings[0], new BaseItemDto { Id = Guid.Parse(peerItem) }, false);

    private static string? PeerKeyOf(MetadataWork offer) => offer.SourceItem.Id?.ToString("N");

    private Task<IReadOnlyDictionary<string, MetadataWork>> NewestAsync(MetadataWork[] offers, params string[] versioned)
    {
        var contested = new Dictionary<string, IReadOnlyList<MetadataWork>>(StringComparer.OrdinalIgnoreCase) { ["/local/movies/a.mkv"] = offers };
        return _settler.NewestOffersAsync(contested, HintKind.Metadata, new HashSet<string>(versioned, StringComparer.OrdinalIgnoreCase), offer => offer.Source, PeerKeyOf, CancellationToken.None);
    }
}
