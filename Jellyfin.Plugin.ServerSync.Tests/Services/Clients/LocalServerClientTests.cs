using System;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Clients;

/// <summary>
/// The local client must write only the history parts it is given, and must turn a missing user, a missing item, or a failing library into "not found" so one item never stops a sync.
/// </summary>
public sealed class LocalServerClientTests
{
    private static readonly DateTime Watched = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private readonly ILibraryManager _libraryManager;
    private readonly InterfaceStub _library;
    private readonly IUserManager _userManager;
    private readonly InterfaceStub _users;
    private readonly IUserDataManager _userDataManager;
    private readonly InterfaceStub _userData;
    private readonly User _user = new("alice", "Default", "Default");
    private readonly Movie _movie = new() { Id = Guid.NewGuid(), Path = "/media/movies/film.mkv" };
    private readonly UserItemData _data = new() { Key = "film", Played = true, PlayCount = 3, PlaybackPositionTicks = 500, LastPlayedDate = Watched, IsFavorite = true };
    private readonly LocalServerClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalServerClientTests"/> class over a library that holds one user, one film, and that user's history for it.
    /// </summary>
    public LocalServerClientTests()
    {
        (_libraryManager, _library) = InterfaceStub.Create<ILibraryManager>();
        (_userManager, _users) = InterfaceStub.Create<IUserManager>();
        (_userDataManager, _userData) = InterfaceStub.Create<IUserDataManager>();
        _users.Answer(nameof(IUserManager.GetUserById), args => (Guid)args[0]! == _user.Id ? _user : null);
        _library.Answer(nameof(ILibraryManager.GetItemById), args => (Guid)args[0]! == _movie.Id ? _movie : null);
        _library.Answer(nameof(ILibraryManager.FindByPath), args => (string)args[0]! == _movie.Path ? _movie : null);
        _userData.Answer(nameof(IUserDataManager.GetUserData), _ => _data);
        _client = new LocalServerClient(NullLogger<LocalServerClient>.Instance, _libraryManager, _userManager, _userDataManager);
    }

    /// <summary>
    /// The constructor refuses each missing dependency.
    /// True: a broken registration fails when the service is built.
    /// False: it would fail on the first history write.
    /// </summary>
    [Fact]
    public void Constructor_MissingDependency_Throws()
    {
        var logger = NullLogger<LocalServerClient>.Instance;

        Assert.Throws<ArgumentNullException>(() => new LocalServerClient(null!, _libraryManager, _userManager, _userDataManager));
        Assert.Throws<ArgumentNullException>(() => new LocalServerClient(logger, null!, _userManager, _userDataManager));
        Assert.Throws<ArgumentNullException>(() => new LocalServerClient(logger, _libraryManager, null!, _userDataManager));
        Assert.Throws<ArgumentNullException>(() => new LocalServerClient(logger, _libraryManager, _userManager, null!));
    }

    /// <summary>
    /// An item is found by its file path as a file, never as a folder.
    /// True: a film is matched to its own row.
    /// False: a folder that shares the path would be taken for the film.
    /// </summary>
    [Fact]
    public void GetItemByPath_KnownAndUnknownPath_FindsTheFileOrNull()
    {
        var found = _client.GetItemByPath(_movie.Path);
        var missing = _client.GetItemByPath("/media/movies/other.mkv");

        Assert.Same(_movie, found);
        Assert.Null(missing);
        Assert.All(_library.Calls, call => Assert.Equal(false, call.Args[1]));
        Assert.Throws<ArgumentNullException>(() => _client.GetItemByPath(null!));
    }

    /// <summary>
    /// A library or user manager that throws reads as not found.
    /// True: one unreadable item is skipped and the sync goes on.
    /// False: one bad lookup would end the whole History run.
    /// </summary>
    [Fact]
    public void Lookups_ManagerThrows_ReadAsNotFound()
    {
        _library.Answer(nameof(ILibraryManager.FindByPath), _ => throw new InvalidOperationException("library busy"));
        _library.Answer(nameof(ILibraryManager.GetItemById), _ => throw new InvalidOperationException("library busy"));
        _users.Answer(nameof(IUserManager.GetUserById), _ => throw new InvalidOperationException("users busy"));

        Assert.Null(_client.GetItemByPath(_movie.Path));
        Assert.Null(_client.GetUser(_user.Id));
        Assert.Null(_client.GetUserItemData(_user.Id, _movie.Id));
        Assert.Null(_client.GetUserItemDataByPath(_user.Id, _movie.Path));
        Assert.Empty(_client.GetLibraryItems(_movie.Id));
        Assert.Empty(_client.GetLibraryItemsWithUserData(_user.Id, _movie.Id));
    }

    /// <summary>
    /// A user is found by id, and an unknown id is null.
    /// True: a mapping to a deleted user is skipped.
    /// False: a deleted user would throw in the middle of a sync.
    /// </summary>
    [Fact]
    public void GetUser_KnownAndUnknownId_ReturnsTheUserOrNull()
    {
        Assert.Same(_user, _client.GetUser(_user.Id));
        Assert.Null(_client.GetUser(Guid.NewGuid()));
    }

    /// <summary>
    /// History is read for a known user and item, by id and by path, and is null when either is missing.
    /// True: a row for a deleted user or item reads as having no history here.
    /// False: history would be read for a null user or item and throw.
    /// </summary>
    [Fact]
    public void GetUserItemData_UserAndItem_BothMustExist()
    {
        Assert.Same(_data, _client.GetUserItemData(_user.Id, _movie.Id));
        Assert.Same(_data, _client.GetUserItemDataByPath(_user.Id, _movie.Path));
        Assert.Null(_client.GetUserItemData(Guid.NewGuid(), _movie.Id));
        Assert.Null(_client.GetUserItemData(_user.Id, Guid.NewGuid()));
        Assert.Null(_client.GetUserItemDataByPath(Guid.NewGuid(), _movie.Path));
        Assert.Null(_client.GetUserItemDataByPath(_user.Id, "/media/movies/other.mkv"));
        Assert.Throws<ArgumentNullException>(() => _client.GetUserItemDataByPath(_user.Id, null!));
    }

    /// <summary>
    /// Every part given is written and saved once as a user data update.
    /// True: a merged history state lands on the local item in full.
    /// False: part of the state would be dropped, or nothing saved.
    /// </summary>
    [Fact]
    public void UpdateUserItemData_AllParts_AreWrittenAndSaved()
    {
        var later = Watched.AddDays(1);

        var saved = _client.UpdateUserItemData(_user.Id, _movie.Id, false, 7, 900, later, false);

        Assert.True(saved);
        Assert.False(_data.Played);
        Assert.Equal(7, _data.PlayCount);
        Assert.Equal(900, _data.PlaybackPositionTicks);
        Assert.Equal(later, _data.LastPlayedDate);
        Assert.False(_data.IsFavorite);
        var save = Assert.Single(_userData.Calls, c => c.Name == nameof(IUserDataManager.SaveUserData));
        Assert.Same(_user, save.Args[0]);
        Assert.Same(_movie, save.Args[1]);
        Assert.Same(_data, save.Args[2]);
        Assert.Equal(UserDataSaveReason.UpdateUserData, save.Args[3]);
    }

    /// <summary>
    /// A null part leaves that part of the history as it is.
    /// True: syncing only favorites never touches the play count or position.
    /// False: every partial write would reset the rest of the user's history.
    /// </summary>
    [Fact]
    public void UpdateUserItemData_NullParts_AreLeftAlone()
    {
        var saved = _client.UpdateUserItemData(_user.Id, _movie.Id, null, null, null, null, null);

        Assert.True(saved);
        Assert.True(_data.Played);
        Assert.Equal(3, _data.PlayCount);
        Assert.Equal(500, _data.PlaybackPositionTicks);
        Assert.Equal(Watched, _data.LastPlayedDate);
        Assert.True(_data.IsFavorite);
    }

    /// <summary>
    /// A null last played date removes the stored date only when asked to clear it.
    /// True: an item marked unplayed loses its date, as Jellyfin records it.
    /// False: an unplayed item would keep a last played date, or a write of other parts would erase it.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void UpdateUserItemData_NullDate_IsClearedOnlyWhenAsked(bool clear, bool expectKept)
    {
        _client.UpdateUserItemData(_user.Id, _movie.Id, false, null, null, null, null, clearLastPlayedDate: clear);

        Assert.Equal(expectKept, _data.LastPlayedDate.HasValue);
    }

    /// <summary>
    /// A write for a missing user, item, or history, or one whose save throws, reports false.
    /// True: the row is marked failed and the run goes on.
    /// False: a failed write would be recorded as applied, or would stop the run.
    /// </summary>
    [Theory]
    [InlineData("no-user")]
    [InlineData("no-item")]
    [InlineData("no-data")]
    [InlineData("save-throws")]
    public void Writes_NothingToWriteOrSaveFails_ReportFalse(string scenario)
    {
        var userId = scenario == "no-user" ? Guid.NewGuid() : _user.Id;
        var itemId = scenario == "no-item" ? Guid.NewGuid() : _movie.Id;
        if (scenario == "no-data")
        {
            _userData.Answer(nameof(IUserDataManager.GetUserData), _ => null);
        }

        if (scenario == "save-throws")
        {
            _userData.Answer(nameof(IUserDataManager.SaveUserData), _ => throw new InvalidOperationException("database locked"));
        }

        Assert.False(_client.UpdateUserItemData(userId, itemId, true, 1, 0, Watched, true));
        Assert.False(_client.SetPlayed(userId, itemId, true, Watched));
        Assert.False(_client.SetFavorite(userId, itemId, true));
        Assert.Equal(scenario == "save-throws" ? 3 : 0, _userData.Calls.Count(c => c.Name == nameof(IUserDataManager.SaveUserData)));
    }

    /// <summary>
    /// Marking played stores the date, and marking unplayed ignores a date and keeps the stored one.
    /// True: a play carries when it happened, and an unmark never invents a play date.
    /// False: an unplayed item would be stamped with a new last played date.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void SetPlayed_Date_IsStoredOnlyWhenPlayed(bool played, bool expectNewDate)
    {
        var given = Watched.AddDays(5);

        var saved = _client.SetPlayed(_user.Id, _movie.Id, played, given);

        Assert.True(saved);
        Assert.Equal(played, _data.Played);
        Assert.Equal(expectNewDate ? given : Watched, _data.LastPlayedDate);
        Assert.Equal(3, _data.PlayCount);
    }

    /// <summary>
    /// Marking played without a date keeps the stored date.
    /// True: a play with no known time does not erase the last one.
    /// False: the last played date would be lost.
    /// </summary>
    [Fact]
    public void SetPlayed_NoDate_KeepsTheStoredDate()
    {
        _client.SetPlayed(_user.Id, _movie.Id, true);

        Assert.Equal(Watched, _data.LastPlayedDate);
    }

    /// <summary>
    /// Setting the favorite state changes that alone and saves.
    /// True: a favorite syncs without touching play state.
    /// False: toggling a favorite would mark the item played or unplayed.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetFavorite_State_ChangesOnlyTheFavorite(bool isFavorite)
    {
        var saved = _client.SetFavorite(_user.Id, _movie.Id, isFavorite);

        Assert.True(saved);
        Assert.Equal(isFavorite, _data.IsFavorite);
        Assert.True(_data.Played);
        Assert.Single(_userData.Calls, c => c.Name == nameof(IUserDataManager.SaveUserData));
    }

    /// <summary>
    /// A library lists every file under it at any depth, and no folders.
    /// True: History covers every episode of every season.
    /// False: only top level files would sync, or folders would be handed out as media.
    /// </summary>
    [Fact]
    public void GetLibraryItems_NestedFolders_ListsOnlyTheFiles()
    {
        var episode = new Movie { Id = Guid.NewGuid() };
        var library = new ClientLibraryFolder { Id = Guid.NewGuid() }
            .With(_movie)
            .With(new ClientLibraryFolder { Id = Guid.NewGuid() }.With(episode));
        _library.Answer(nameof(ILibraryManager.GetItemById), _ => library);

        var items = _client.GetLibraryItems(library.Id);

        Assert.Equal(new BaseItem[] { _movie, episode }.OrderBy(i => i.Id), items.OrderBy(i => i.Id));
    }

    /// <summary>
    /// An id that names no library, or names something that is not a folder, lists nothing.
    /// True: a mapping to a deleted library is skipped.
    /// False: a film's id passed as a library would throw.
    /// </summary>
    [Fact]
    public void GetLibraryItems_NotALibrary_ListsNothing()
    {
        Assert.Empty(_client.GetLibraryItems(Guid.NewGuid()));
        Assert.Empty(_client.GetLibraryItems(_movie.Id));
    }

    /// <summary>
    /// Each file of a library comes back with the user's history for it, and a missing user gets none.
    /// True: a full History scan pairs every local file with its state.
    /// False: history would be read for a user who no longer exists.
    /// </summary>
    [Fact]
    public void GetLibraryItemsWithUserData_KnownAndUnknownUser_PairsFilesWithHistoryOrReturnsNone()
    {
        var library = new ClientLibraryFolder { Id = Guid.NewGuid() }.With(_movie);
        _library.Answer(nameof(ILibraryManager.GetItemById), _ => library);

        var pairs = _client.GetLibraryItemsWithUserData(_user.Id, library.Id);
        var none = _client.GetLibraryItemsWithUserData(Guid.NewGuid(), library.Id);

        var pair = Assert.Single(pairs);
        Assert.Same(_movie, pair.Item);
        Assert.Same(_data, pair.UserData);
        Assert.Empty(none);
    }
}
