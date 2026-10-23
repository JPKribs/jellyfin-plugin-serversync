using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Playlists;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.ServerSync.Tests.Playlists.PlaylistTestHarness;

namespace Jellyfin.Plugin.ServerSync.Tests.Playlists;

/// <summary>
/// <see cref="PlaylistMirror"/> and <see cref="PeerPlaylistReader"/>: which local playlist is the copy
/// of a source playlist, whose it is, and what the mirror writes. The Jellyfin managers are recording
/// stubs and the copy registry is a real SQLite store, so every create, update and delete the mirror
/// would make is visible.
/// </summary>
[Collection(PlaylistCopyStoreCollection.Name)]
public sealed class PlaylistMirrorTests : IDisposable
{
    private readonly PlaylistTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private SourceServerClient Client(StubPeerHandler handler, SourceServer server)
        => new(NullLogger<SourceServerClient>.Instance, new HttpClient(handler), server.Url, server.ApiKey, "test", "0.0.0-test");

    private Task<PlaylistMirrorOutcome> Apply(SourcePlaylist source, bool syncShares = true)
        => _h.Mirror.ApplyAsync(_h.Server, source, _h.UserMap, syncShares, CancellationToken.None, _h.LocalItems.OfType<Playlist>().ToList());

    private PlaylistCreationRequest SingleCreate() => (PlaylistCreationRequest)Assert.Single(_h.CallsTo(_h.Playlists, nameof(IPlaylistManager.CreatePlaylist)))[0]!;

    // -----------------------------------------------------------------------
    // Identity: root keys, copies, owners
    // -----------------------------------------------------------------------

    /// <summary>
    /// A playlist's root is its own id unless it is a copy: then the registry's root wins, and the
    /// provider tag is the fallback when the registry has no entry.
    /// True: every server names one playlist family by one key, so copies of copies stay one family.
    /// False: a copy reports itself as a new root and the peer makes a copy of the copy.
    /// </summary>
    [Fact]
    public void RootKey_OwnId_ThenTag_ThenRegistry()
    {
        var root = Guid.NewGuid();
        var registered = Guid.NewGuid();
        var own = _h.AddPlaylist("own", _h.Owner.Id);
        var tagged = _h.AddPlaylist("tagged", _h.Owner.Id, taggedRoot: root.ToString("D"));
        var both = _h.AddPlaylist("both", _h.Owner.Id, taggedRoot: root.ToString("N"));
        PlaylistCopyStore.Set(both.Id, registered.ToString("D"), ServerKey);

        Assert.Equal(N(own.Id), PlaylistMirror.RootKey(own));
        Assert.False(PlaylistMirror.IsMirror(own));
        Assert.Equal(N(root), PlaylistMirror.RootKey(tagged));
        Assert.True(PlaylistMirror.IsMirror(tagged));
        Assert.Equal(N(registered), PlaylistMirror.RootKey(both));
    }

    /// <summary>
    /// A tag that is not a Guid does not make a playlist a copy.
    /// True: a stray or hand-edited provider id never turns a user's playlist into something the prune may delete.
    /// False: a garbage tag makes a user's own playlist a deletable copy.
    /// </summary>
    [Fact]
    public void IsMirror_IgnoresATagThatIsNotAGuid()
    {
        var playlist = _h.AddPlaylist("odd", _h.Owner.Id, taggedRoot: "not-a-guid");

        Assert.False(PlaylistMirror.IsMirror(playlist));
        Assert.Equal(N(playlist.Id), PlaylistMirror.RootKey(playlist));
    }

    /// <summary>
    /// The server a copy came from is the registry's, then the tag's, and an original has none.
    /// True: the prune of one server only ever considers copies that server made.
    /// False: a server's prune deletes another server's copies, or a user's own playlists.
    /// </summary>
    [Fact]
    public void CopyServer_RegistryThenTag_OriginalHasNone()
    {
        var original = _h.AddPlaylist("own", _h.Owner.Id);
        var tagged = _h.AddPlaylist("tagged", _h.Owner.Id, taggedRoot: N(Guid.NewGuid()), taggedServer: "server-b");
        var registered = _h.AddPlaylist("registered", _h.Owner.Id, taggedServer: "server-b");
        PlaylistCopyStore.Set(registered.Id, N(Guid.NewGuid()), ServerKey);

        Assert.Null(PlaylistMirror.CopyServer(original));
        Assert.Equal("server-b", PlaylistMirror.CopyServer(tagged));
        Assert.Equal(ServerKey, PlaylistMirror.CopyServer(registered));
    }

    /// <summary>
    /// A source original matches only a local copy tagged with its root. A source copy also matches the
    /// local original it was copied from.
    /// True: a Sync pair edits one playlist on both sides, and a coincidental id never pairs two unrelated playlists.
    /// False: a copy coming back makes a second copy here, or an original is overwritten by an unrelated playlist.
    /// </summary>
    [Fact]
    public void FindByRoot_MatchesCopies_AndOriginalsOnlyForASourceCopy()
    {
        var original = _h.AddPlaylist("mine", _h.Owner.Id);
        var root = Guid.NewGuid();
        var copy = _h.AddPlaylist("theirs", _h.Owner.Id, taggedRoot: N(root));
        var local = _h.LocalItems.OfType<Playlist>().ToList();

        Assert.Same(copy, _h.Mirror.FindByRoot(N(root), sourceIsOriginal: true, local));
        Assert.Null(_h.Mirror.FindByRoot(N(original.Id), sourceIsOriginal: true, local));
        Assert.Same(original, _h.Mirror.FindByRoot(N(original.Id), sourceIsOriginal: false, local));
        Assert.Null(_h.Mirror.FindByRoot(N(Guid.NewGuid()), sourceIsOriginal: false, local));
    }

    /// <summary>
    /// An empty registry is rebuilt from the copies' tags, skipping playlists whose tags are incomplete,
    /// and a registry that has entries is left alone.
    /// True: an upgrade from tag-only copies, or a reset database, still knows its copies.
    /// False: after a reset every copy reads as a user's playlist, or a rebuild overwrites a live registry.
    /// </summary>
    [Fact]
    public void RebuildCopiesFromTags_OnlyWhenEmpty_AndOnlyCompleteTags()
    {
        var root = Guid.NewGuid();
        var full = _h.AddPlaylist("full", _h.Owner.Id, taggedRoot: root.ToString("D"), taggedServer: ServerKey);
        var noServer = _h.AddPlaylist("no server", _h.Owner.Id, taggedRoot: N(Guid.NewGuid()));
        var badRoot = _h.AddPlaylist("bad root", _h.Owner.Id, taggedRoot: "x", taggedServer: ServerKey);
        _h.AddPlaylist("own", _h.Owner.Id);

        Assert.Equal(1, _h.Mirror.RebuildCopiesFromTags());
        Assert.Equal(new PlaylistCopy(N(root), ServerKey), PlaylistCopyStore.Get(full.Id));
        Assert.Null(PlaylistCopyStore.Get(noServer.Id));
        Assert.Null(PlaylistCopyStore.Get(badRoot.Id));

        _h.AddPlaylist("later", _h.Owner.Id, taggedRoot: N(Guid.NewGuid()), taggedServer: ServerKey);
        Assert.Equal(0, _h.Mirror.RebuildCopiesFromTags());
        Assert.Equal(1, PlaylistCopyStore.Count());
    }

    /// <summary>
    /// The user map holds enabled mappings with valid ids whose local user exists, keyed by the source
    /// id in N form.
    /// True: a playlist is only ever given to a real, mapped local user.
    /// False: a disabled or dangling mapping hands a playlist to the wrong user, or the apply throws on a missing owner.
    /// </summary>
    [Fact]
    public void UserMap_KeepsOnlyUsableMappings()
    {
        var gone = Guid.NewGuid();
        _h.Server.UserMappings.Add(new UserMapping { SourceUserId = N(SourceStranger), LocalUserId = N(gone) });
        _h.Server.UserMappings.Add(new UserMapping { SourceUserId = "not-a-guid", LocalUserId = N(_h.Owner.Id) });
        _h.Server.UserMappings[1].IsEnabled = false;

        var map = _h.UserMap;

        Assert.Equal(_h.Owner.Id, Assert.Single(map).Value);
        Assert.True(map.ContainsKey(N(SourceOwner)));
    }

    // -----------------------------------------------------------------------
    // Reading the peer
    // -----------------------------------------------------------------------

    /// <summary>
    /// The peer listing keeps playlists of mapped owners with valid ids, normalises every id, falls back
    /// to the playlist id for a missing root, and drops shares with invalid user ids.
    /// True: only mapped users' playlists are mirrored, each under one stable root.
    /// False: an unmapped user's playlist throws in the apply, or one family gets two roots.
    /// </summary>
    [Fact]
    public async Task ReadAsync_PeerListing_FiltersToMappedOwners_AndNormalises()
    {
        var root = Guid.NewGuid();
        var mine = StubPeerHandler.Playlist("Mine", SourceOwner, root: root, isCopy: true, paths: "/src/movies/a.mkv");
        mine.Shares.Add(new PeerPlaylistShare { UserId = SourceViewer.ToString("D"), CanEdit = true });
        mine.Shares.Add(new PeerPlaylistShare { UserId = "bogus" });
        var noRoot = StubPeerHandler.Playlist("No root", SourceViewer);
        var stranger = StubPeerHandler.Playlist("Stranger", SourceStranger);
        var badId = StubPeerHandler.Playlist("Bad", SourceOwner);
        badId.Id = "nope";
        var handler = new StubPeerHandler { Playlists = new PeerPlaylistsResponse { Playlists = { mine, noRoot, stranger, badId } } };

        var read = await _h.Mirror.ReadAsync(_h.Server, Client(handler, _h.Server), _h.UserMap, null, CancellationToken.None);

        Assert.Equal(new[] { "Mine", "No root" }, read.Select(p => p.Name));
        var first = read[0];
        Assert.Equal(N(root), first.RootId);
        Assert.Equal(N(SourceOwner), first.OwnerUserId);
        Assert.True(first.OwnerKnown);
        Assert.True(first.IsCopy);
        Assert.Equal(new[] { (N(SourceViewer), true) }, first.Shares);
        Assert.Equal(new[] { "/src/movies/a.mkv" }, first.EntryPaths);
        Assert.Equal(read[1].Id, read[1].RootId);
        Assert.True(read[1].IsOriginal);
    }

    /// <summary>
    /// With a standard user's key every playlist listed is given to that user, and only that user's own
    /// playlists count as having a known owner.
    /// True: a standard user's key copies what that user can see, into that user's account, without touching shares.
    /// False: playlists shared with the user are copied under their real owner's account, or with the owner's shares.
    /// </summary>
    [Fact]
    public async Task ReadAsync_StandardUserKey_AssignsEverythingToThatUser()
    {
        _h.Server.AccessLevel = SourceServer.StandardUserAccess;
        _h.Server.AuthenticatedUserId = N(SourceOwner);
        var own = StubPeerHandler.Playlist("Own", SourceOwner);
        var shared = StubPeerHandler.Playlist("Shared with me", SourceStranger);
        var handler = new StubPeerHandler { Playlists = new PeerPlaylistsResponse { Playlists = { own, shared } } };

        var read = await _h.Mirror.ReadAsync(_h.Server, Client(handler, _h.Server), _h.UserMap, null, CancellationToken.None);

        Assert.All(read, p => Assert.Equal(N(SourceOwner), p.OwnerUserId));
        Assert.True(read.Single(p => p.Name == "Own").OwnerKnown);
        Assert.False(read.Single(p => p.Name == "Shared with me").OwnerKnown);
    }

    /// <summary>
    /// An administrator's key against a peer that will not list its playlists fails the read instead
    /// of reading as an empty listing.
    /// True: a refused listing leaves every local copy as it is.
    /// False: the refusal reads as "no playlists" and the copies are pruned.
    /// </summary>
    [Fact]
    public async Task ReadAsync_PeerRefuses_WithAdminKey_Throws()
    {
        var handler = new StubPeerHandler { PlaylistsStatus = HttpStatusCode.Forbidden };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _h.Mirror.ReadAsync(_h.Server, Client(handler, _h.Server), _h.UserMap, null, CancellationToken.None));
    }

    /// <summary>
    /// A peer on another protocol version answers 426, which surfaces as an HTTP error carrying that status.
    /// True: the task can tell "wait for both versions to match" apart from a real failure.
    /// False: a version mismatch fails the whole playlist run as an error.
    /// </summary>
    [Fact]
    public async Task ReadAsync_VersionMismatch_Throws426()
    {
        var handler = new StubPeerHandler { PlaylistsStatus = (HttpStatusCode)426 };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => _h.Mirror.ReadAsync(_h.Server, Client(handler, _h.Server), _h.UserMap, null, CancellationToken.None));
        Assert.Equal(426, (int?)ex.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Applying
    // -----------------------------------------------------------------------

    /// <summary>
    /// A new playlist is created for the mapped owner with the entries that exist here, in order,
    /// translated through the library mapping, and registered as a copy of its root from this server.
    /// Entries outside every mapping and entries not here are dropped.
    /// True: the copy holds exactly the local items, belongs to the right user, and is recognised next run.
    /// False: the copy goes to the wrong user, holds items out of order, or is made again every run.
    /// </summary>
    [Fact]
    public async Task Apply_NewPlaylist_CreatesForMappedOwner_WithResolvedItemsInOrder()
    {
        var b = _h.AddMovie("/local/movies/b.mkv");
        var a = _h.AddMovie("/local/movies/a.mkv");
        var source = Source("Road trip", paths: new List<string?> { "/src/movies/b.mkv", "/elsewhere/x.mkv", "/src/movies/missing.mkv", null, "/src/movies/a.mkv" });

        var outcome = await Apply(source);

        Assert.Equal(PlaylistMirrorOutcome.Created, outcome);
        var request = SingleCreate();
        Assert.Equal("Road trip", request.Name);
        Assert.Equal(_h.Owner.Id, request.UserId);
        Assert.Equal(new[] { b.Id, a.Id }, request.ItemIdList);
        Assert.Equal(Jellyfin.Data.Enums.MediaType.Video, request.MediaType);
        Assert.Equal(new PlaylistCopy(source.RootId, ServerKey), PlaylistCopyStore.Get(_h.CreatedId));
        Assert.DoesNotContain(_h.Library.Calls, c => c.Name == nameof(ILibraryManager.FindByPath) && (string)c.Args[0]! == "/elsewhere/x.mkv");
    }

    /// <summary>
    /// Shares map to local users: the owner and unmapped users are left out, a user listed twice can
    /// edit if any entry says so, and the public flag is copied. With sharing off neither is copied.
    /// True: the copy is visible to the same people as the original, and no one else.
    /// False: an unmapped user id lands in the share list, or sharing off still makes the copy public.
    /// </summary>
    [Fact]
    public async Task Apply_NewPlaylist_MapsShares_OnlyWhenSharingIsOn()
    {
        var shares = new List<(string UserId, bool CanEdit)>
        {
            (N(SourceViewer), false),
            (N(SourceViewer), true),
            (N(SourceOwner), true),
            (N(SourceStranger), true)
        };

        await Apply(Source("Shared", openAccess: true, shares: shares));
        var on = SingleCreate();
        Assert.True(on.Public);
        var share = Assert.Single(on.Users);
        Assert.Equal(_h.Viewer.Id, share.UserId);
        Assert.True(share.CanEdit);

        _h.Playlists.Calls.Clear();
        _h.CreatedId = Guid.NewGuid();
        await Apply(Source("Shared again", openAccess: true, shares: shares), syncShares: false);
        var off = SingleCreate();
        Assert.False(off.Public);
        Assert.Empty(off.Users);
    }

    /// <summary>
    /// A playlist whose entries all exist elsewhere waits instead of being created empty, and an empty
    /// source playlist is created empty.
    /// True: a playlist arrives once its items do, and an empty one is still mirrored.
    /// False: an empty shell is made before the content syncs, or an empty playlist is never mirrored.
    /// </summary>
    [Fact]
    public async Task Apply_NewPlaylist_WaitsForItems_ButCreatesAnEmptyOne()
    {
        var waiting = await Apply(Source("Not here yet", paths: new List<string?> { "/src/movies/later.mkv" }));
        Assert.Equal(PlaylistMirrorOutcome.Waiting, waiting);
        Assert.Empty(_h.CallsTo(_h.Playlists, nameof(IPlaylistManager.CreatePlaylist)));

        var empty = await Apply(Source("Empty"));
        Assert.Equal(PlaylistMirrorOutcome.Created, empty);
        Assert.Empty(SingleCreate().ItemIdList);
    }

    /// <summary>
    /// A local original that belongs to someone other than the mapped owner is never rewritten.
    /// True: a user's own playlist is safe from a mapping that points at it from another account.
    /// False: the mirror renames or reowns a playlist the user made here.
    /// </summary>
    [Fact]
    public async Task Apply_LocalOriginalOfAnotherUser_IsSkipped()
    {
        var mine = _h.AddPlaylist("Viewer's own", _h.Viewer.Id);

        var outcome = await Apply(Source("Renamed", id: N(mine.Id), isCopy: true));

        Assert.Equal(PlaylistMirrorOutcome.Skipped, outcome);
        Assert.Equal(_h.Viewer.Id, mine.OwnerUserId);
        Assert.Empty(_h.Playlists.Calls);
    }

    /// <summary>
    /// A copy whose owner changed on the source moves to the new mapped owner when the owner is known.
    /// True: handing a playlist to another user there hands the copy over here.
    /// False: the copy stays with the old owner forever.
    /// </summary>
    [Fact]
    public async Task Apply_CopyWithChangedOwner_MovesToTheMappedOwner()
    {
        var root = N(Guid.NewGuid());
        var copy = _h.AddPlaylist("List", _h.Viewer.Id, taggedRoot: root, taggedServer: ServerKey);

        var outcome = await Apply(Source("List", root: root));

        Assert.Equal(PlaylistMirrorOutcome.Updated, outcome);
        Assert.Equal(_h.Owner.Id, copy.OwnerUserId);
        Assert.Single(_h.CallsTo(_h.Playlists, nameof(IPlaylistManager.SavePlaylistFile)));
        Assert.Single(_h.CallsTo(_h.Library, nameof(ILibraryManager.UpdateItemAsync)));
        Assert.Empty(_h.CallsTo(_h.Playlists, nameof(IPlaylistManager.UpdatePlaylist)));
    }

    /// <summary>
    /// With a standard user's key the owner is not known, so a copy with another owner is left alone.
    /// True: a key that cannot see owners never reassigns a playlist.
    /// False: every copy is moved to the key's user.
    /// </summary>
    [Fact]
    public async Task Apply_CopyWithChangedOwner_OwnerUnknown_IsSkipped()
    {
        var root = N(Guid.NewGuid());
        var copy = _h.AddPlaylist("List", _h.Viewer.Id, taggedRoot: root, taggedServer: ServerKey);

        var outcome = await Apply(Source("List", root: root, ownerKnown: false));

        Assert.Equal(PlaylistMirrorOutcome.Skipped, outcome);
        Assert.Equal(_h.Viewer.Id, copy.OwnerUserId);
    }

    /// <summary>
    /// A copy that already matches is left untouched, and a renamed source renames only the name.
    /// True: a quiet run writes nothing, so it raises no hints and no file churn.
    /// False: every run rewrites every playlist, or a rename resets the items.
    /// </summary>
    [Fact]
    public async Task Apply_ExistingCopy_UnchangedOrRenamed()
    {
        var root = N(Guid.NewGuid());
        _h.AddPlaylist("List", _h.Owner.Id, taggedRoot: root, taggedServer: ServerKey);

        Assert.Equal(PlaylistMirrorOutcome.Unchanged, await Apply(Source("List", root: root)));
        Assert.Empty(_h.Playlists.Calls);

        Assert.Equal(PlaylistMirrorOutcome.Updated, await Apply(Source("List, renamed", root: root)));
        var update = (PlaylistUpdateRequest)Assert.Single(_h.CallsTo(_h.Playlists, nameof(IPlaylistManager.UpdatePlaylist)))[0]!;
        Assert.Equal("List, renamed", update.Name);
        Assert.Null(update.Ids);
        Assert.Null(update.Users);
        Assert.Null(update.Public);
    }

    /// <summary>
    /// Shares and the public flag are brought in line when sharing is on and the owner is known, and
    /// left as they are otherwise.
    /// True: a share added there reaches the copy, and a standard user's key never rewrites shares.
    /// False: shares drift apart forever, or a key that cannot see shares wipes them here.
    /// </summary>
    [Fact]
    public async Task Apply_ExistingCopy_UpdatesSharesOnlyWhenKnown()
    {
        var root = N(Guid.NewGuid());
        _h.AddPlaylist("List", _h.Owner.Id, taggedRoot: root, taggedServer: ServerKey);
        var shares = new List<(string UserId, bool CanEdit)> { (N(SourceViewer), true) };

        Assert.Equal(PlaylistMirrorOutcome.Unchanged, await Apply(Source("List", root: root, openAccess: true, shares: shares, ownerKnown: false)));

        Assert.Equal(PlaylistMirrorOutcome.Updated, await Apply(Source("List", root: root, openAccess: true, shares: shares)));
        var update = (PlaylistUpdateRequest)Assert.Single(_h.CallsTo(_h.Playlists, nameof(IPlaylistManager.UpdatePlaylist)))[0]!;
        Assert.True(update.Public);
        Assert.Equal(_h.Viewer.Id, Assert.Single(update.Users!).UserId);
    }

    /// <summary>
    /// Deleting a copy deletes the playlist and forgets its registration.
    /// True: a deleted copy is not recognised again, and its file goes with it.
    /// False: the registry keeps a dead id, or the playlist file is left on disk.
    /// </summary>
    [Fact]
    public void Delete_RemovesThePlaylist_AndItsRegistration()
    {
        var copy = _h.AddPlaylist("List", _h.Owner.Id);
        PlaylistCopyStore.Set(copy.Id, N(Guid.NewGuid()), ServerKey);

        _h.Mirror.Delete(copy);

        var args = Assert.Single(_h.CallsTo(_h.Library, nameof(ILibraryManager.DeleteItem)));
        Assert.Same(copy, args[0]);
        Assert.True(((DeleteOptions)args[1]!).DeleteFileLocation);
        Assert.Null(PlaylistCopyStore.Get(copy.Id));
    }

    // -----------------------------------------------------------------------
    // PeerPlaylistReader, the other side of ReadAsync
    // -----------------------------------------------------------------------

    /// <summary>
    /// The peer listing reports each playlist with its root, copy flag and owner, filtered by id and by
    /// owner when asked, and an unknown scoped user sees nothing.
    /// True: the peer learns which playlists are copies, and a scoped key cannot list everyone's playlists.
    /// False: a copy is listed as an original and copied back, or a bad user id lists every playlist.
    /// </summary>
    [Fact]
    public void PeerPlaylistReader_ReportsRootsAndFilters()
    {
        var root = Guid.NewGuid();
        var original = _h.AddPlaylist("Mine", _h.Owner.Id);
        var copy = _h.AddPlaylist("Copy", _h.Viewer.Id, taggedRoot: N(root));
        var reader = new PeerPlaylistReader(_h.LibraryManager, _h.UserManager);

        var all = reader.ReadAll();
        Assert.Equal(2, all.Playlists.Count);
        var listedCopy = all.Playlists.Single(p => p.Name == "Copy");
        Assert.True(listedCopy.IsCopy);
        Assert.Equal(N(root), listedCopy.RootId);
        Assert.Equal(N(_h.Viewer.Id), listedCopy.OwnerUserId);
        var listedOriginal = all.Playlists.Single(p => p.Name == "Mine");
        Assert.False(listedOriginal.IsCopy);
        Assert.Equal(N(original.Id), listedOriginal.RootId);

        Assert.Equal("Copy", Assert.Single(reader.ReadAll(onlyId: copy.Id).Playlists).Name);
        Assert.Equal("Mine", Assert.Single(reader.ReadAll(owners: new[] { _h.Owner.Id }).Playlists).Name);
        Assert.Empty(reader.ReadAll(scopedUserId: Guid.NewGuid()).Playlists);
    }
}
