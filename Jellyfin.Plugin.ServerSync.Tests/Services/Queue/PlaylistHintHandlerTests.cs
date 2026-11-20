using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// A playlist hint copies or deletes the right local playlist, keeps a newer edit made here, and never touches another user's playlist.
/// </summary>
[Collection(QueueStateCollection.Name)]
public sealed class PlaylistHintHandlerTests : IDisposable
{
    private const string ThisServer = "this-server";
    private const string PlaylistsPath = "/Sync/Peer/Playlists";
    private static readonly DateTime Edited = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly PlaylistTestHarness _h = new();
    private readonly FakeConfigManager _config = QueueSamples.Config();
    private readonly QueuePeerHandler _peer = new();
    private readonly Guid _root = Guid.NewGuid();
    private readonly VersionStore _versions;
    private readonly OutboundHintStore _outbound;
    private readonly PlaylistHintHandler _handler;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaylistHintHandlerTests"/> class.
    /// </summary>
    public PlaylistHintHandlerTests()
    {
        var provider = new FixedProvider(_h.Database);
        _versions = new VersionStore(provider, NullLogger<VersionStore>.Instance);
        _outbound = new OutboundHintStore(provider, NullLogger<OutboundHintStore>.Instance);
        var host = QueueSamples.Host(ThisServer);
        var resolver = new VersionConflictResolver(_versions, host, _config, NullLogger<VersionConflictResolver>.Instance);
        var worker = new OutboundHintWorker(_outbound, _config, null!, null!, null!, new HintSuppression(), null!, NullLogger<OutboundHintWorker>.Instance);
        var publisher = new LocalHintPublisher(_config, _outbound, _versions, worker, host, NullLogger<LocalHintPublisher>.Instance);
        _handler = new PlaylistHintHandler(_config, _h.Mirror, resolver, publisher, NullLogger<PlaylistHintHandler>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose() => _h.Dispose();

    private string RootKey => HintProtocol.PlaylistKey(_root);

    private InboundHint Hint(string itemType = HintProtocol.PlaylistOriginal, DateTime? at = null, string? userId = null) => new()
    {
        HintId = "origin-id:1",
        OriginServerId = "origin-id",
        Kind = HintKind.Playlists,
        Key = RootKey,
        ItemId = RootKey,
        ItemType = itemType,
        UserId = userId ?? PlaylistTestHarness.N(PlaylistTestHarness.SourceOwner),
        VersionServerId = "origin-id",
        VersionTimestamp = at ?? Edited,
        ReceivedAt = DateTime.UtcNow,
        NextAttempt = DateTime.UtcNow
    };

    private SourceServer Origin(ServerMode mode = ServerMode.Pull)
    {
        var origin = _h.NewServer();
        origin.Mode = mode;
        return origin;
    }

    private async Task<HintApplyResult> ApplyAsync(InboundHint hint, SourceServer? origin = null)
    {
        using var client = _peer.Client();
        return await _handler.ApplyAsync(hint, origin ?? Origin(), client, CancellationToken.None);
    }

    private Playlist AddCopy(Guid owner) => _h.AddPlaylist("Road Trip", owner, taggedRoot: RootKey, taggedServer: PlaylistTestHarness.ServerKey);

    private int Deletions => _h.CallsTo(_h.Library, nameof(ILibraryManager.DeleteItem)).Count;

    private void PeerLists(params string[] entryPaths)
        => _peer.OnJson(PlaylistsPath, new PeerPlaylistsResponse
        {
            Playlists =
            {
                new PeerPlaylist
                {
                    Id = RootKey,
                    Name = "Road Trip",
                    MediaType = "Video",
                    OwnerUserId = PlaylistTestHarness.N(PlaylistTestHarness.SourceOwner),
                    Entries = entryPaths.Select(path => new PeerPlaylistEntry { ItemId = Guid.NewGuid().ToString("N"), Path = path }).ToList()
                }
            }
        });

    /// <summary>
    /// A missing collaborator or argument is refused.
    /// True: a wiring mistake fails at startup and a bad call is named.
    /// False: a playlist hint ends in a null reference while the Playlists lock is held.
    /// </summary>
    [Fact]
    public async Task ConstructorAndApply_NullArgument_Throw()
    {
        using var client = _peer.Client();

        Assert.Throws<ArgumentNullException>(() => new PlaylistHintHandler(null!, _h.Mirror, null!, null!, NullLogger<PlaylistHintHandler>.Instance));
        Assert.Throws<ArgumentNullException>(() => new PlaylistHintHandler(_config, null!, null!, null!, NullLogger<PlaylistHintHandler>.Instance));
        Assert.Throws<ArgumentNullException>(() => new PlaylistHintHandler(_config, _h.Mirror, null!, null!, NullLogger<PlaylistHintHandler>.Instance));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _handler.ApplyAsync(null!, Origin(), client, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _handler.ApplyAsync(Hint(), null!, client, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _handler.ApplyAsync(Hint(), Origin(), null!, CancellationToken.None));
    }

    /// <summary>
    /// With Playlists off the hint is dropped and the origin is not asked.
    /// True: turning Playlists off stops copies and deletions at once.
    /// False: a hint queued before the switch still deletes a playlist.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_PlaylistsOff_DropsAsModuleOff()
    {
        _config.Configuration.EnablePlaylistSync = false;

        var result = await ApplyAsync(Hint(HintProtocol.PlaylistDeleted));

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.ModuleOff, result.Code);
        Assert.Empty(_peer.Requests);
    }

    /// <summary>
    /// A hint whose key or playlist id is not an id is dropped as invalid.
    /// True: a malformed row ends at once.
    /// False: a deletion runs against a key that matches nothing, or everything.
    /// </summary>
    [Theory]
    [InlineData("not-an-id", "55555555555555555555555555555555")]
    [InlineData("44444444444444444444444444444444", "not-an-id")]
    [InlineData("44444444444444444444444444444444", null)]
    public async Task ApplyAsync_IdsMalformed_DropsAsInvalid(string key, string? itemId)
    {
        var hint = Hint();
        hint.Key = key;
        hint.ItemId = itemId;

        var result = await ApplyAsync(hint);

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.Invalid, result.Code);
    }

    /// <summary>
    /// From a Sync origin, a newer edit recorded here is kept and the origin is not read.
    /// True: an older edit arriving late cannot undo a newer one made on this side.
    /// False: the last hint to arrive wins, whatever its age.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_SyncOriginAndNewerEditHere_KeepsTheLocalPlaylist()
    {
        _versions.Set(new ObjectVersion { Kind = HintKind.Playlists, Key = RootKey, ServerId = "third-server", Timestamp = Edited.AddMinutes(5) });
        AddCopy(_h.Owner.Id);

        var result = await ApplyAsync(Hint(HintProtocol.PlaylistDeleted), Origin(ServerMode.Sync));

        Assert.Equal(HintApplyOutcome.Unchanged, result.Outcome);
        Assert.Equal(HintCode.Superseded, result.Code);
        Assert.Equal(0, Deletions);
        Assert.Empty(_peer.Requests);
        Assert.Empty(_outbound.GetAll());
    }

    /// <summary>
    /// A newer edit that was made on this server is also announced back.
    /// True: the origin, which just sent an older value, is told to take the newer one.
    /// False: the two servers keep different playlists until the next scheduled sync.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_SyncOriginAndNewerEditMadeHere_AnnouncesTheLocalPlaylist()
    {
        var origin = Origin(ServerMode.Sync);
        _config.Configuration.Servers.Add(origin);
        _versions.Set(new ObjectVersion { Kind = HintKind.Playlists, Key = RootKey, ServerId = ThisServer, Timestamp = Edited.AddMinutes(5) });
        var mine = AddCopy(_h.Owner.Id);

        var result = await ApplyAsync(Hint(), origin);

        var announced = Assert.Single(_outbound.GetAll());
        Assert.Equal(HintCode.Superseded, result.Code);
        Assert.Equal(origin.Key, announced.PeerKey);
        Assert.Equal(RootKey, announced.Key);
        Assert.Equal(mine.Id.ToString("N"), announced.ItemId);
        Assert.Equal(HintProtocol.PlaylistCopy, announced.ItemType);
        Assert.Equal(Edited.AddMinutes(5), announced.VersionTimestamp);
    }

    /// <summary>
    /// From a Pull origin the hint applies even over a newer edit recorded here.
    /// True: a server this one only pulls from stays the source of truth for its playlists.
    /// False: one local edit blocks a Pull source's playlist for good.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_PullOriginAndNewerEditHere_StillApplies()
    {
        _versions.Set(new ObjectVersion { Kind = HintKind.Playlists, Key = RootKey, ServerId = "third-server", Timestamp = Edited.AddMinutes(5) });
        AddCopy(_h.Owner.Id);

        var result = await ApplyAsync(Hint(HintProtocol.PlaylistDeleted), Origin(ServerMode.Pull));

        Assert.Equal(HintApplyOutcome.Applied, result.Outcome);
        Assert.Equal(1, Deletions);
    }

    /// <summary>
    /// A deletion with no local copy writes nothing and records the deletion's stamp.
    /// True: the playlist does not come back here from a third server with an older stamp.
    /// False: a deleted playlist is copied again by the next hint that mentions it.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_DeletionWithNoLocalCopy_RecordsTheStampAndChangesNothing()
    {
        var result = await ApplyAsync(Hint(HintProtocol.PlaylistDeleted));

        var stamp = _versions.Get(HintKind.Playlists, RootKey);
        Assert.Equal(HintApplyOutcome.Unchanged, result.Outcome);
        Assert.Equal(HintCode.Unchanged, result.Code);
        Assert.Equal("origin-id", stamp?.ServerId);
        Assert.Equal(Edited, stamp?.Timestamp);
        Assert.Empty(_peer.Requests);
    }

    /// <summary>
    /// A deletion deletes the local copy that belongs to the mapped owner, and records the stamp.
    /// True: a playlist deleted on one server goes from the other.
    /// False: deleted playlists live on, or come back.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_DeletionOfTheMappedOwnersCopy_DeletesIt()
    {
        var copy = AddCopy(_h.Owner.Id);

        var result = await ApplyAsync(Hint(HintProtocol.PlaylistDeleted));

        var deleted = Assert.Single(_h.CallsTo(_h.Library, nameof(ILibraryManager.DeleteItem)));
        Assert.Equal(HintApplyOutcome.Applied, result.Outcome);
        Assert.Same(copy, deleted[0]);
        Assert.NotNull(_versions.Get(HintKind.Playlists, RootKey));
    }

    /// <summary>
    /// A deletion never deletes a playlist that belongs to another user, or one whose sender owner is not mapped.
    /// True: one user's deletion on a peer cannot remove someone else's playlist here.
    /// False: a peer can delete any playlist on this server by naming its root.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplyAsync_DeletionOfAnotherUsersPlaylist_KeepsIt(bool senderOwnerMapped)
    {
        AddCopy(senderOwnerMapped ? _h.Viewer.Id : _h.Owner.Id);
        var hint = Hint(HintProtocol.PlaylistDeleted, userId: senderOwnerMapped ? null : PlaylistTestHarness.N(PlaylistTestHarness.SourceStranger));

        var result = await ApplyAsync(hint);

        Assert.Equal(HintApplyOutcome.Unchanged, result.Outcome);
        Assert.Equal(HintCode.NotMapped, result.Code);
        Assert.Equal(0, Deletions);
    }

    /// <summary>
    /// With Removed playlists set to Keep, a deletion keeps the local copy and still records the stamp.
    /// True: the setting is honoured, and the kept playlist is not sent back to the server that deleted it.
    /// False: Keep is ignored, or the kept copy resurrects the playlist on its origin.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_DeletionWithRemovedPlaylistsSetToKeep_KeepsTheCopyAndRecordsTheStamp()
    {
        _config.Configuration.PlaylistRemovedAction = PlaylistRemovedAction.Keep;
        AddCopy(_h.Owner.Id);

        var result = await ApplyAsync(Hint(HintProtocol.PlaylistDeleted));

        Assert.Equal(HintApplyOutcome.Unchanged, result.Outcome);
        Assert.Equal(HintCode.Superseded, result.Code);
        Assert.Equal(0, Deletions);
        Assert.NotNull(_versions.Get(HintKind.Playlists, RootKey));
    }

    /// <summary>
    /// An origin that fails the playlist read, cannot be reached, or does not list playlists at all, is retried later.
    /// True: a peer that is restarting costs a retry, not the change.
    /// False: the hint is dropped as if the playlist were gone.
    /// </summary>
    [Theory]
    [InlineData("error")]
    [InlineData("unreachable")]
    [InlineData("no-server-sync")]
    public async Task ApplyAsync_OriginCannotListThePlaylist_RetriesLater(string failure)
    {
        if (failure == "error")
        {
            _peer.OnStatus(PlaylistsPath, HttpStatusCode.InternalServerError);
        }
        else if (failure == "unreachable")
        {
            _peer.OnUnreachable(PlaylistsPath);
        }

        var result = await ApplyAsync(Hint());

        Assert.Equal(HintApplyOutcome.Retry, result.Outcome);
        Assert.NotNull(result.Reason);
        Assert.Null(_versions.Get(HintKind.Playlists, RootKey));
    }

    /// <summary>
    /// A playlist the origin no longer lists is dropped as gone.
    /// True: an edit to a playlist deleted since is not retried.
    /// False: the hint retries for a day against nothing.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_OriginNoLongerListsThePlaylist_DropsAsSourceGone()
    {
        _peer.OnJson(PlaylistsPath, new PeerPlaylistsResponse());

        var result = await ApplyAsync(Hint());

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.SourceGone, result.Code);
    }

    /// <summary>
    /// The origin is asked for the one playlist the hint names, in this server's name.
    /// True: one hint reads one playlist.
    /// False: every hint lists every playlist on the origin.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_ReadsThePlaylist_AsksForThatPlaylistOnly()
    {
        _peer.OnJson(PlaylistsPath, new PeerPlaylistsResponse());

        await ApplyAsync(Hint());

        var request = Assert.Single(_peer.Requests);
        Assert.Contains("id=" + RootKey, request.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("serverId=" + ThisServer, request.Query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A playlist none of whose items are here yet is kept waiting, with no playlist made and no stamp recorded.
    /// True: the scheduled task copies it once its items arrive.
    /// False: an empty shell is created, or the stamp is recorded and the playlist is never copied.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_NoneOfItsItemsAreHereYet_WaitsWithoutRecording()
    {
        PeerLists("/src/movies/later.mkv");

        var result = await ApplyAsync(Hint());

        Assert.Equal(HintApplyOutcome.Unchanged, result.Outcome);
        Assert.Equal(HintCode.NoLocalItem, result.Code);
        Assert.Empty(_h.CallsTo(_h.Playlists, nameof(IPlaylistManager.CreatePlaylist)));
        Assert.Null(_versions.Get(HintKind.Playlists, RootKey));
    }

    /// <summary>
    /// A new playlist is copied for the mapped owner, and a hand made edit's stamp is recorded while a provider's is not.
    /// True: the playlist appears here and a later conflict is decided on the edit it came from.
    /// False: the copy is not made, or provider work is stamped as an edit and beats real ones.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplyAsync_NewPlaylistOnTheOrigin_CopiesItAndRecordsOnlyAHandMadeEdit(bool recorded)
    {
        PeerLists();
        var hint = Hint();
        hint.Recorded = recorded;

        var result = await ApplyAsync(hint);

        var created = (MediaBrowser.Model.Playlists.PlaylistCreationRequest)Assert.Single(_h.CallsTo(_h.Playlists, nameof(IPlaylistManager.CreatePlaylist)))[0]!;
        Assert.Equal(HintApplyOutcome.Applied, result.Outcome);
        Assert.Equal("Road Trip", created.Name);
        Assert.Equal(_h.Owner.Id, created.UserId);
        Assert.Equal(recorded, _versions.Get(HintKind.Playlists, RootKey) is not null);
    }

    /// <summary>
    /// A cancelled apply stops and writes nothing.
    /// True: a server shutting down ends the pass at once.
    /// False: a playlist is deleted after the apply was cancelled.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_CancelledToken_ThrowsAndChangesNothing()
    {
        AddCopy(_h.Owner.Id);
        using var client = _peer.Client();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _handler.ApplyAsync(Hint(HintProtocol.PlaylistDeleted), Origin(), client, cancelled.Token));

        Assert.Equal(0, Deletions);
    }
}
