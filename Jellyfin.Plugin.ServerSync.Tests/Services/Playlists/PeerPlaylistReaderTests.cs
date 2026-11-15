using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Services.Playlists;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Playlists;

/// <summary>
/// The peer playlist reader must list this server's playlists as they are, and only those a scoped user may see.
/// </summary>
public sealed class PeerPlaylistReaderTests
{
    private readonly User _owner = new("owner", "Default", "Default");
    private readonly User _viewer = new("viewer", "Default", "Default");
    private readonly List<BaseItem> _items = new();
    private readonly ILibraryManager _library;
    private readonly InterfaceStub _libraryStub;
    private readonly IUserManager _users;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerPlaylistReaderTests"/> class.
    /// </summary>
    public PeerPlaylistReaderTests()
    {
        (_library, _libraryStub) = InterfaceStub.Create<ILibraryManager>();
        _libraryStub.Answer(nameof(ILibraryManager.GetItemList), _ => new List<BaseItem>(_items));
        (_users, var usersStub) = InterfaceStub.Create<IUserManager>();
        usersStub.Answer(nameof(IUserManager.GetUserById), args => new[] { _owner, _viewer }.FirstOrDefault(u => u.Id == (Guid)args[0]!));
    }

    /// <summary>
    /// The reader needs the library and the users, and runs without a link registry.
    /// True: a missing manager shows at construction.
    /// False: the first peer to ask for playlists would get a server error.
    /// </summary>
    [Fact]
    public void Constructor_NullManagers_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new PeerPlaylistReader(null!, _users));
        Assert.Throws<ArgumentNullException>(() => new PeerPlaylistReader(_library, null!));
        Assert.NotNull(new PeerPlaylistReader(_library, _users, null));
    }

    /// <summary>
    /// A scoped user this server does not know gets an empty answer, and the library is never listed.
    /// True: an unknown user id cannot be used to list anyone's playlists.
    /// False: a bad id would fall through to the unscoped listing.
    /// </summary>
    [Fact]
    public void ReadAll_UnknownScopedUser_ListsNothing()
    {
        _items.Add(new Playlist { Id = Guid.NewGuid(), Name = "Open", OwnerUserId = _owner.Id, OpenAccess = true });

        var response = new PeerPlaylistReader(_library, _users).ReadAll(scopedUserId: Guid.NewGuid());

        Assert.Empty(response.Playlists);
        Assert.Empty(_libraryStub.Calls);
    }

    /// <summary>
    /// A server with no playlists, or whose listing holds only other items, answers with an empty list.
    /// True: a peer reads an empty server without an error.
    /// False: a film in the listing would be sent as a playlist, or an empty server would throw.
    /// </summary>
    /// <param name="hasOtherItem">Whether the listing holds an item that is not a playlist.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadAll_NoPlaylists_IsEmpty(bool hasOtherItem)
    {
        if (hasOtherItem)
        {
            _items.Add(new Movie { Id = Guid.NewGuid(), Name = "Film" });
        }

        var response = new PeerPlaylistReader(_library, _users).ReadAll();

        Assert.NotNull(response);
        Assert.Empty(response.Playlists);
    }

    /// <summary>
    /// A playlist goes out with its ids in the dashless form, its media type, its open access flag, and each share with its edit right.
    /// True: the peer can rebuild the playlist's sharing exactly.
    /// False: a share would lose its edit right, or ids would not match the peer's user mappings.
    /// </summary>
    [Fact]
    public void ReadAll_Playlist_CarriesItsSharingAndIds()
    {
        var id = Guid.NewGuid();
        _items.Add(new Playlist
        {
            Id = id,
            Name = "Shared",
            OwnerUserId = _owner.Id,
            OpenAccess = true,
            PlaylistMediaType = Jellyfin.Data.Enums.MediaType.Audio,
            Shares = new[] { new PlaylistUserPermissions(_viewer.Id, true), new PlaylistUserPermissions(Guid.Empty, false) }
        });

        var playlist = Assert.Single(new PeerPlaylistReader(_library, _users, new NoLinks()).ReadAll().Playlists);

        Assert.Equal(id.ToString("N"), playlist.Id);
        Assert.Equal(id.ToString("N"), playlist.RootId);
        Assert.False(playlist.IsCopy);
        Assert.Equal("Shared", playlist.Name);
        Assert.Equal("Audio", playlist.MediaType);
        Assert.Equal(_owner.Id.ToString("N"), playlist.OwnerUserId);
        Assert.True(playlist.OpenAccess);
        Assert.Equal(new[] { (_viewer.Id.ToString("N"), true), (Guid.Empty.ToString("N"), false) }, playlist.Shares.Select(s => (s.UserId, s.CanEdit)));
        Assert.Empty(playlist.Entries);
    }

    /// <summary>
    /// A playlist with no name goes out with an empty one.
    /// True: the peer's reader never meets a null name.
    /// False: one unnamed playlist would break the whole listing for the peer.
    /// </summary>
    [Fact]
    public void ReadAll_UnnamedPlaylist_HasAnEmptyName()
    {
        _items.Add(new Playlist { Id = Guid.NewGuid(), Name = null, OwnerUserId = _owner.Id });

        var playlist = Assert.Single(new PeerPlaylistReader(_library, _users).ReadAll().Playlists);

        Assert.Equal(string.Empty, playlist.Name);
    }

    /// <summary>
    /// A scoped user this server knows is listed their own playlist, with the owner filter still applied.
    /// True: a standard user's key reads that user's playlists.
    /// False: a known scoped user would get the empty answer meant for an unknown one.
    /// </summary>
    [Fact]
    public void ReadAll_KnownScopedUser_ListsTheirOwn()
    {
        _items.Add(new Playlist { Id = Guid.NewGuid(), Name = "Own", OwnerUserId = _viewer.Id });
        _items.Add(new Playlist { Id = Guid.NewGuid(), Name = "Other", OwnerUserId = _owner.Id });

        var response = new PeerPlaylistReader(_library, _users).ReadAll(scopedUserId: _viewer.Id, owners: new[] { _viewer.Id });

        Assert.Equal("Own", Assert.Single(response.Playlists).Name);
    }

    /// <summary>
    /// The id and owner filters hold together, and an empty owner list lists nothing.
    /// True: a peer asking for one user's one playlist gets that and nothing more.
    /// False: a filter would be dropped when another was set, or no owners would mean every owner.
    /// </summary>
    [Fact]
    public void ReadAll_IdAndOwnerFilters_HoldTogether()
    {
        var mine = new Playlist { Id = Guid.NewGuid(), Name = "Mine", OwnerUserId = _owner.Id };
        var theirs = new Playlist { Id = Guid.NewGuid(), Name = "Theirs", OwnerUserId = _viewer.Id };
        _items.Add(mine);
        _items.Add(theirs);
        var reader = new PeerPlaylistReader(_library, _users);

        var match = reader.ReadAll(onlyId: mine.Id, owners: new[] { _owner.Id });
        var mismatch = reader.ReadAll(onlyId: theirs.Id, owners: new[] { _owner.Id });
        var noOwners = reader.ReadAll(owners: Array.Empty<Guid>());

        Assert.Equal("Mine", Assert.Single(match.Playlists).Name);
        Assert.Empty(mismatch.Playlists);
        Assert.Empty(noOwners.Playlists);
    }
}
