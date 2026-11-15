using System;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// One local change becomes one hint for each peer that takes changes, maps the object, and has not declined it, and its stamp is recorded here.
/// </summary>
[Collection(QueueStateCollection.Name)]
public sealed class LocalHintPublisherTests : IDisposable
{
    private const string ThisServer = "this-server";
    private static readonly DateTime Edited = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly QueueTestDatabase _db = new();
    private readonly FakeConfigManager _config = QueueSamples.Config();
    private readonly HintSuppression _suppression = new();
    private readonly VersionStore _versions;
    private readonly OutboundHintStore _outbound;
    private readonly ContentSyncTableManager _content;
    private readonly LocalHintPublisher _publisher;
    private readonly Guid _item = Guid.NewGuid();

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalHintPublisherTests"/> class.
    /// </summary>
    public LocalHintPublisherTests()
    {
        _versions = new VersionStore(_db.Provider, NullLogger<VersionStore>.Instance);
        _outbound = new OutboundHintStore(_db.Provider, NullLogger<OutboundHintStore>.Instance);
        _content = new ContentSyncTableManager(_db.Provider, NullLogger<ContentSyncTableManager>.Instance);
        var worker = new OutboundHintWorker(_outbound, _config, null!, null!, null!, _suppression, null!, NullLogger<OutboundHintWorker>.Instance);
        _publisher = new LocalHintPublisher(_config, _outbound, _versions, worker, QueueSamples.Host(ThisServer), NullLogger<LocalHintPublisher>.Instance, links: null, content: _content);
    }

    /// <inheritdoc/>
    public void Dispose() => _db.Dispose();

    private static string Path => QueueSamples.LocalPath("a.mkv");

    private string ItemKey => HintProtocol.MetadataKey(_item);

    private static ObjectVersion Version() => new() { ServerId = ThisServer, Timestamp = Edited };

    private SourceServer AddPeer(string key = "peer", ServerMode mode = ServerMode.Sync)
    {
        var peer = QueueSamples.Server(key, mode, key + "-id");
        _config.Configuration.Servers.Add(peer);
        return peer;
    }

    /// <summary>
    /// A change published without a stamp is refused, for every kind.
    /// True: a caller's mistake is named.
    /// False: a hint goes out with no version and the peer can never weigh it.
    /// </summary>
    [Fact]
    public void Publish_NullVersion_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _publisher.PublishMetadata(_item, Path, null!, null));
        Assert.Throws<ArgumentNullException>(() => _publisher.PublishPeople("Jane Doe", _item, null!, null));
        Assert.Throws<ArgumentNullException>(() => _publisher.PublishContent(_item, Path, null!, null));
        Assert.Throws<ArgumentNullException>(() => _publisher.PublishPlaylist(_item, "root", "List", QueueSamples.LocalUserId, null!, null, true, false, false));
        Assert.Throws<ArgumentNullException>(() => _publisher.PublishHistory(QueueSamples.LocalUserId, "viewer", _item, Path, null!, null));
    }

    /// <summary>
    /// A hand made metadata edit queues one hint for a peer that maps the item, and records its stamp here.
    /// True: the peer hears of the edit with the stamp it was made at, and this server can defend it.
    /// False: the edit waits for a scan, or a later conflict has no stamp to decide on.
    /// </summary>
    [Fact]
    public void PublishMetadata_PeerMapsTheItem_QueuesOneHintAndRecordsTheStamp()
    {
        AddPeer();

        var queued = _publisher.PublishMetadata(_item, Path, Version(), excludePeerKey: null, itemType: "Movie");

        var row = Assert.Single(_outbound.GetAll());
        Assert.Equal(1, queued);
        Assert.Equal("peer", row.PeerKey);
        Assert.Equal(HintKind.Metadata, row.Kind);
        Assert.Equal(ItemKey, row.Key);
        Assert.Equal(ItemKey, row.ItemId);
        Assert.Equal(Path, row.ItemPath);
        Assert.Equal("Movie", row.ItemType);
        Assert.True(row.Recorded);
        Assert.Equal(ThisServer, row.VersionServerId);
        Assert.Equal(Edited, row.VersionTimestamp);
        Assert.StartsWith(ThisServer + ":", row.HintId, StringComparison.Ordinal);
        var stamp = _versions.Get(HintKind.Metadata, ItemKey);
        Assert.Equal(ThisServer, stamp?.ServerId);
        Assert.Equal(Edited, stamp?.Timestamp);
    }

    /// <summary>
    /// A provider's work is queued as unrecorded and leaves no stamp here.
    /// True: any hand made edit, here or on a peer, still beats a scan's result.
    /// False: a metadata refresh stamps itself as an edit and overwrites real edits elsewhere.
    /// </summary>
    [Fact]
    public void PublishMetadata_ProviderWork_QueuesUnrecordedAndStoresNoStamp()
    {
        AddPeer();

        var queued = _publisher.PublishMetadata(_item, Path, Version(), excludePeerKey: null, recorded: false);

        Assert.Equal(1, queued);
        Assert.False(_outbound.GetAll().Single().Recorded);
        Assert.Null(_versions.Get(HintKind.Metadata, ItemKey));
    }

    /// <summary>
    /// A server this one only pulls from, a disabled one, and one with no key are sent nothing.
    /// True: hints go only to servers that take changes from here.
    /// False: rows are queued for servers that will never be sent to.
    /// </summary>
    [Theory]
    [InlineData(ServerMode.Pull, true, "key")]
    [InlineData(ServerMode.Sync, false, "key")]
    [InlineData(ServerMode.Push, true, "")]
    public void PublishMetadata_PeerDoesNotTakeChanges_QueuesNothing(ServerMode mode, bool enabled, string apiKey)
    {
        var peer = AddPeer(mode: mode);
        peer.IsEnabled = enabled;
        peer.ApiKey = apiKey;

        var queued = _publisher.PublishMetadata(_item, Path, Version(), excludePeerKey: null);

        Assert.Equal(0, queued);
        Assert.Empty(_outbound.GetAll());
    }

    /// <summary>
    /// The peer a change came from is left out, in any case of its key, and the others still hear of it.
    /// True: a change is never sent back to the server it was taken from.
    /// False: two servers pass one change back and forth.
    /// </summary>
    [Fact]
    public void PublishMetadata_ExcludedPeer_IsSkippedAndOthersAreTold()
    {
        AddPeer("peer-a", ServerMode.Push);
        AddPeer("peer-b", ServerMode.Sync);

        var queued = _publisher.PublishMetadata(_item, Path, Version(), excludePeerKey: "PEER-A");

        Assert.Equal(1, queued);
        Assert.Equal("peer-b", _outbound.GetAll().Single().PeerKey);
    }

    /// <summary>
    /// An item outside a peer's mapped libraries queues nothing for it, and the stamp is still recorded.
    /// True: a peer is not told about a library it does not sync, and the edit is still defended here.
    /// False: unmapped hints are sent and declined, or the local stamp is lost with them.
    /// </summary>
    [Fact]
    public void PublishMetadata_ItemOutsideThePeersLibraries_QueuesNothingButRecordsTheStamp()
    {
        AddPeer();

        var queued = _publisher.PublishMetadata(_item, "/local/shows/a.mkv", Version(), excludePeerKey: null);

        Assert.Equal(0, queued);
        Assert.Empty(_outbound.GetAll());
        Assert.NotNull(_versions.Get(HintKind.Metadata, ItemKey));
    }

    /// <summary>
    /// An object the peer declined is not queued for it again.
    /// True: a peer that filters an item is not sent every later edit of it.
    /// False: each edit is sent and declined, forever.
    /// </summary>
    [Fact]
    public void PublishMetadata_PeerDeclinedTheObject_QueuesNothing()
    {
        AddPeer();
        _suppression.Note("peer", HintKind.Metadata, ItemKey, HintCode.Filtered, null, DateTime.UtcNow);

        var queued = _publisher.PublishMetadata(_item, Path, Version(), excludePeerKey: null);

        Assert.Equal(0, queued);
        Assert.Empty(_outbound.GetAll());
    }

    /// <summary>
    /// A person's edit goes to every peer that takes changes, named as given, and is stamped under the folded name.
    /// True: the peer finds the person by the name Jellyfin uses, and the stamp matches however the name is cased.
    /// False: people hints are filtered by library and never sent, or the stamp is missed on the next edit.
    /// </summary>
    [Fact]
    public void PublishPeople_PeerTakesChanges_QueuesUnderTheNameAndStampsTheFoldedName()
    {
        var peer = AddPeer();
        peer.LibraryMappings.Clear();
        var person = Guid.NewGuid();
        var name = "Jane Doe " + person.ToString("N");

        var queued = _publisher.PublishPeople(name, person, Version(), excludePeerKey: null);

        var row = Assert.Single(_outbound.GetAll());
        Assert.Equal(1, queued);
        Assert.Equal(HintKind.People, row.Kind);
        Assert.Equal(name, row.Key);
        Assert.Equal(name, row.UserName);
        Assert.Equal(person.ToString("N"), row.ItemId);
        Assert.NotNull(_versions.Get(HintKind.People, HintProtocol.PeopleKey(name)));
    }

    /// <summary>
    /// A person with no name is refused.
    /// True: a nameless person never becomes a hint with an empty key.
    /// False: the peer receives a hint it can only answer as invalid.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void PublishPeople_BlankName_Throws(string name)
    {
        Assert.Throws<ArgumentException>(() => _publisher.PublishPeople(name, _item, Version(), excludePeerKey: null));
    }

    /// <summary>
    /// A new media file is announced under the item's id with its type.
    /// True: a peer learns of a file the moment it appears and can fetch it.
    /// False: new files wait for the peer's next scan.
    /// </summary>
    [Fact]
    public void PublishContent_NewFile_QueuesAContentHint()
    {
        AddPeer();

        var queued = _publisher.PublishContent(_item, Path, Version(), excludePeerKey: null, itemType: "Movie");

        var row = Assert.Single(_outbound.GetAll());
        Assert.Equal(1, queued);
        Assert.Equal(HintKind.Content, row.Kind);
        Assert.Equal(ItemKey, row.Key);
        Assert.Equal(Path, row.ItemPath);
        Assert.Equal("Movie", row.ItemType);
    }

    /// <summary>
    /// A .strm is never announced as content, and leaves no stamp.
    /// True: a pointer file a peer cannot copy is kept out of the queue.
    /// False: peers are told to download a file that is only a link.
    /// </summary>
    [Theory]
    [InlineData("/local/movies/a.strm")]
    [InlineData("/local/movies/a.STRM")]
    public void PublishContent_StrmFile_QueuesNothing(string path)
    {
        AddPeer();

        var queued = _publisher.PublishContent(_item, path, Version(), excludePeerKey: null);

        Assert.Equal(0, queued);
        Assert.Empty(_outbound.GetAll());
        Assert.Null(_versions.Get(HintKind.Content, ItemKey));
    }

    /// <summary>
    /// A file this server was given by a peer is not announced when it arrives, and is announced as removed when it leaves.
    /// True: the peer that owns a file announces it, and a server that only links to this copy still hears it has gone.
    /// False: every synced file is announced again by each server that receives it, or a removal is never heard.
    /// </summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void PublishContent_FileSyncedFromAPeer_IsAnnouncedOnlyWhenItLeaves(bool removed, int expected)
    {
        AddPeer();
        _content.Upsert(new SyncItem
        {
            SourceItemId = "source-item",
            SourceLibraryId = "lib",
            LocalLibraryId = "local",
            SourcePath = "/media/movies/a.mkv",
            LocalPath = Path,
            ServerKey = "another-server",
            Status = SyncStatus.Synced,
            StatusDate = DateTime.UtcNow
        });

        var queued = _publisher.PublishContent(_item, Path, Version(), excludePeerKey: null, itemType: "Movie", removed: removed);

        Assert.Equal(expected, queued);
        Assert.Equal(expected, _outbound.GetAll().Count);
        Assert.All(_outbound.GetAll(), row => Assert.Equal(HintProtocol.ContentRemoved, row.ItemType));
    }

    /// <summary>
    /// A playlist change carries the item type that says whether it is a copy and whether it was deleted.
    /// True: the peer copies, or deletes, the right playlist.
    /// False: a deletion is read as an edit and the playlist comes back.
    /// </summary>
    [Theory]
    [InlineData(false, false, HintProtocol.PlaylistOriginal)]
    [InlineData(true, false, HintProtocol.PlaylistCopy)]
    [InlineData(false, true, HintProtocol.PlaylistDeleted)]
    [InlineData(true, true, HintProtocol.PlaylistCopyDeleted)]
    public void PublishPlaylist_OwnerMapped_QueuesWithTheMatchingItemType(bool copy, bool deleted, string expectedType)
    {
        AddPeer();
        var root = HintProtocol.PlaylistKey(Guid.NewGuid());

        var queued = _publisher.PublishPlaylist(_item, root, "Road Trip", QueueSamples.LocalUserId, Version(), excludePeerKey: null, recorded: true, deleted: deleted, copy: copy);

        var row = Assert.Single(_outbound.GetAll());
        Assert.Equal(1, queued);
        Assert.Equal(HintKind.Playlists, row.Kind);
        Assert.Equal(root, row.Key);
        Assert.Equal(_item.ToString("N"), row.ItemId);
        Assert.Equal(expectedType, row.ItemType);
        Assert.Equal(QueueSamples.LocalUserId.ToString("N"), row.UserId);
        Assert.Equal("Road Trip", row.UserName);
    }

    /// <summary>
    /// A playlist whose owner the peer does not map queues nothing for it.
    /// True: a peer is never told of a playlist it could not give to anyone.
    /// False: the hint is sent and declined on every edit.
    /// </summary>
    [Fact]
    public void PublishPlaylist_OwnerNotMappedOnThePeer_QueuesNothing()
    {
        AddPeer();

        var queued = _publisher.PublishPlaylist(_item, HintProtocol.PlaylistKey(Guid.NewGuid()), "Road Trip", Guid.NewGuid(), Version(), excludePeerKey: null, recorded: true, deleted: false, copy: false);

        Assert.Equal(0, queued);
        Assert.Empty(_outbound.GetAll());
    }

    /// <summary>
    /// A playlist's deletion is announced even while the playlist keeps echoing after applies.
    /// True: a deleted playlist goes from every server.
    /// False: the loop breaker swallows the deletion and the playlist returns from a peer.
    /// </summary>
    [Fact]
    public void PublishPlaylist_DeletionWhileTheLoopBreakerHolds_IsStillAnnounced()
    {
        AddPeer();
        var root = HintProtocol.PlaylistKey(Guid.NewGuid());
        new ApplyGuard().Enter(HintProtocol.GuardKey(HintKind.Playlists, root)).Dispose();
        int Publish(bool deleted) => _publisher.PublishPlaylist(_item, root, "Road Trip", QueueSamples.LocalUserId, Version(), excludePeerKey: null, recorded: true, deleted: deleted, copy: false);
        for (var i = 0; i < LoopBreaker.Limit; i++)
        {
            Publish(deleted: false);
        }

        var held = Publish(deleted: false);
        var deletion = Publish(deleted: true);

        Assert.Equal(-1, held);
        Assert.Equal(1, deletion);
        Assert.Equal(HintProtocol.PlaylistDeleted, _outbound.GetAll().Single().ItemType);
    }

    /// <summary>
    /// A history change goes to a peer that maps both the user and the item, keyed by the two local ids.
    /// True: the peer can find the user and the item the play belongs to.
    /// False: the play is sent under a key the peer cannot translate.
    /// </summary>
    [Fact]
    public void PublishHistory_PeerMapsUserAndItem_QueuesAHistoryHint()
    {
        AddPeer();

        var queued = _publisher.PublishHistory(QueueSamples.LocalUserId, "viewer", _item, Path, Version(), excludePeerKey: null, itemType: "Movie+Played");

        var row = Assert.Single(_outbound.GetAll());
        var key = HintProtocol.HistoryKey(QueueSamples.LocalUserId, _item);
        Assert.Equal(1, queued);
        Assert.Equal(HintKind.History, row.Kind);
        Assert.Equal(key, row.Key);
        Assert.Equal(_item.ToString("N"), row.ItemId);
        Assert.Equal(QueueSamples.LocalUserId.ToString("N"), row.UserId);
        Assert.Equal("viewer", row.UserName);
        Assert.Equal("Movie+Played", row.ItemType);
        Assert.NotNull(_versions.Get(HintKind.History, key));
    }

    /// <summary>
    /// A play by a user the peer does not map queues nothing, and its stamp is still recorded.
    /// True: one user's history never reaches a server that has no such user, and a later conflict can still be decided.
    /// False: the play is sent and declined, or the stamp is lost because nobody was told.
    /// </summary>
    [Fact]
    public void PublishHistory_UserNotMappedOnThePeer_QueuesNothingButRecordsTheStamp()
    {
        AddPeer();
        var stranger = Guid.NewGuid();

        var queued = _publisher.PublishHistory(stranger, "stranger", _item, Path, Version(), excludePeerKey: null);

        Assert.Equal(0, queued);
        Assert.Empty(_outbound.GetAll());
        Assert.NotNull(_versions.Get(HintKind.History, HintProtocol.HistoryKey(stranger, _item)));
    }

    /// <summary>
    /// With no servers configured a change queues nothing and still records its stamp.
    /// True: a server on its own keeps the history of its edits for the day a peer is added.
    /// False: publishing throws inside Jellyfin's save event.
    /// </summary>
    [Fact]
    public void PublishMetadata_NoServersConfigured_QueuesNothing()
    {
        var queued = _publisher.PublishMetadata(_item, Path, Version(), excludePeerKey: null);

        Assert.Equal(0, queued);
        Assert.NotNull(_versions.Get(HintKind.Metadata, ItemKey));
    }
}
