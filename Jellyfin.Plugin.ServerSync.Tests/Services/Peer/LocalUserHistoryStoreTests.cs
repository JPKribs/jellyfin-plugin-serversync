using System;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Peer;

/// <summary>
/// The store must read a user's history as the state a peer negotiates on, and write an agreed state back with an unplayed item losing its date.
/// </summary>
public sealed class LocalUserHistoryStoreTests
{
    private static readonly DateTime Watched = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private readonly InterfaceStub _userData;
    private readonly User _user = new("alice", "Default", "Default");
    private readonly Movie _movie = new() { Id = Guid.NewGuid() };
    private readonly UserItemData _data = new() { Key = "film", Played = true, PlayCount = 3, PlaybackPositionTicks = 500, LastPlayedDate = Watched, IsFavorite = true };
    private readonly LocalUserHistoryStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalUserHistoryStoreTests"/> class over one user, one film, and that user's history for it.
    /// </summary>
    public LocalUserHistoryStoreTests()
    {
        var (libraryManager, library) = InterfaceStub.Create<ILibraryManager>();
        var (userManager, users) = InterfaceStub.Create<IUserManager>();
        var (userDataManager, userData) = InterfaceStub.Create<IUserDataManager>();
        _userData = userData;
        users.Answer(nameof(IUserManager.GetUserById), args => (Guid)args[0]! == _user.Id ? _user : null);
        library.Answer(nameof(ILibraryManager.GetItemById), args => (Guid)args[0]! == _movie.Id ? _movie : null);
        userData.Answer(nameof(IUserDataManager.GetUserData), _ => _data);
        _store = new LocalUserHistoryStore(new LocalServerClient(NullLogger<LocalServerClient>.Instance, libraryManager, userManager, userDataManager));
    }

    /// <summary>
    /// The constructor refuses a missing local client.
    /// True: a broken registration fails when the service is built.
    /// False: the first negotiation would fail with a null reference.
    /// </summary>
    [Fact]
    public void Constructor_MissingClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LocalUserHistoryStore(null!));
    }

    /// <summary>
    /// A read returns every part of the user's history for the item.
    /// True: the peer compares against what this server really holds.
    /// False: a part left out would read as "no value" and the peer's value would overwrite it.
    /// </summary>
    [Fact]
    public void Read_KnownUserAndItem_ReturnsEveryPart()
    {
        var state = _store.Read(_user.Id, _movie.Id);

        Assert.True(state!.Played);
        Assert.Equal(3, state.PlayCount);
        Assert.Equal(500, state.PlaybackPositionTicks);
        Assert.Equal(Watched, state.LastPlayedDate);
        Assert.True(state.IsFavorite);
    }

    /// <summary>
    /// A read for a user or an item this server does not hold is null.
    /// True: the peer is told "not found" and keeps its row.
    /// False: a missing item would read as an unplayed one and the peer would be told to unmark it.
    /// </summary>
    [Fact]
    public void Read_UnknownUserOrItem_IsNull()
    {
        Assert.Null(_store.Read(Guid.NewGuid(), _movie.Id));
        Assert.Null(_store.Read(_user.Id, Guid.NewGuid()));
    }

    /// <summary>
    /// Writing a played state stores every part and keeps its date.
    /// True: an agreed play lands here with when it happened.
    /// False: the agreed state would be applied in part.
    /// </summary>
    [Fact]
    public void Write_PlayedState_StoresEveryPart()
    {
        var later = Watched.AddDays(2);

        var written = _store.Write(_user.Id, _movie.Id, new PeerHistoryState { Played = true, PlayCount = 9, PlaybackPositionTicks = 0, LastPlayedDate = later, IsFavorite = false });

        Assert.True(written);
        Assert.Equal(9, _data.PlayCount);
        Assert.Equal(0, _data.PlaybackPositionTicks);
        Assert.Equal(later, _data.LastPlayedDate);
        Assert.False(_data.IsFavorite);
        Assert.Single(_userData.Calls, c => c.Name == nameof(IUserDataManager.SaveUserData));
    }

    /// <summary>
    /// A state with no date clears the stored date only when it says the item is unplayed.
    /// True: an agreed unmark leaves no last played date, and a state that says nothing about play keeps it.
    /// False: an unplayed item would keep a play date, or a favorite only write would erase one.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(null, true)]
    public void Write_StateWithoutADate_ClearsTheDateOnlyWhenUnplayed(bool? played, bool expectDateKept)
    {
        _store.Write(_user.Id, _movie.Id, new PeerHistoryState { Played = played });

        Assert.Equal(expectDateKept, _data.LastPlayedDate.HasValue);
    }

    /// <summary>
    /// A write for a user or an item this server does not hold reports false and saves nothing, and a missing state is refused.
    /// True: the peer is told the write did not happen.
    /// False: the peer would record an agreement this server never applied.
    /// </summary>
    [Fact]
    public void Write_UnknownUserOrItem_ReportsFalse()
    {
        var state = new PeerHistoryState { Played = true };

        Assert.False(_store.Write(Guid.NewGuid(), _movie.Id, state));
        Assert.False(_store.Write(_user.Id, Guid.NewGuid(), state));
        Assert.DoesNotContain(_userData.Calls, c => c.Name == nameof(IUserDataManager.SaveUserData));
        Assert.Throws<ArgumentNullException>(() => _store.Write(_user.Id, _movie.Id, null!));
    }
}
