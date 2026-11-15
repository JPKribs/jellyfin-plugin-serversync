using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.HistorySync;

/// <summary>
/// A history row is built only for an item this server holds, for a user this server has, and inside the Library Filter.
/// </summary>
public sealed class HistorySyncTableServiceTests
{
    private static readonly Guid LocalUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LocalItemId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Played = new(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);

    private readonly IUserManager _userManager;
    private readonly InterfaceStub _users;
    private readonly IUserDataManager _userDataManager;
    private readonly InterfaceStub _userData;
    private readonly ILocalItems _localItems;
    private readonly InterfaceStub _items;
    private readonly HistorySyncTableService _service;
    private readonly User _user = new("alice", "Default", "Default");

    public HistorySyncTableServiceTests()
    {
        (_userManager, _users) = InterfaceStub.Create<IUserManager>();
        (_userDataManager, _userData) = InterfaceStub.Create<IUserDataManager>();
        (_localItems, _items) = InterfaceStub.Create<ILocalItems>();
        _service = new HistorySyncTableService(NullLogger<HistorySyncTableService>.Instance, _userManager, _userDataManager, _localItems);
    }

    private static UserMapping Users(string? localUserId = null)
        => new() { SourceUserId = "source-user", LocalUserId = localUserId ?? LocalUserId.ToString("D") };

    private static LibraryMapping Library() => new()
    {
        SourceLibraryId = "source-lib",
        LocalLibraryId = "local-lib",
        SourceRootPath = "/media/movies",
        LocalRootPath = "/local/movies"
    };

    private static BaseItemDto SourceItem(string? path = "/media/movies/Film/film.mkv", string? name = "Film") => new()
    {
        Path = path,
        Name = name,
        UserData = new UserItemDataDto { Played = true, PlayCount = 3, PlaybackPositionTicks = 100, LastPlayedDate = Played, IsFavorite = true }
    };

    private void HoldUserAndItem()
    {
        _users.Answer("GetUserById", _ => _user);
        _items.Answer("FindByPath", _ => new Movie { Id = LocalItemId, Path = "/local/movies/Film/film.mkv" });
    }

    /// <summary>
    /// Each dependency is required.
    /// True: a broken registration fails at startup with the argument named.
    /// False: the service would build and fail later in the middle of a scan.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Constructor_NullDependency_Throws(int missing)
    {
        Assert.Throws<ArgumentNullException>(() => new HistorySyncTableService(
            missing == 0 ? null! : NullLogger<HistorySyncTableService>.Instance,
            missing == 1 ? null! : _userManager,
            missing == 2 ? null! : _userDataManager,
            missing == 3 ? null! : _localItems));
    }

    /// <summary>
    /// A mapping whose local user id is not an id finds no user and asks the server nothing.
    /// True: a damaged mapping is skipped quietly.
    /// False: a bad id would throw and stop the whole history scan.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-an-id")]
    public void FindLocalUser_IdIsNotAnId_ReturnsNullWithoutALookup(string localUserId)
    {
        var user = _service.FindLocalUser(Users(localUserId));

        Assert.Null(user);
        Assert.Empty(_users.Calls);
    }

    /// <summary>
    /// A user this server has is returned, looked up by the mapping's local id, and one it does not have gives null.
    /// True: history is written for the mapped user and skipped for a deleted one.
    /// False: history would be written to another user, or a deleted user would stop the scan.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FindLocalUser_ValidId_ReturnsTheUserTheServerHolds(bool exists)
    {
        _users.Answer("GetUserById", _ => exists ? _user : null);

        var user = _service.FindLocalUser(Users());

        Assert.Equal(exists ? _user : null, user);
        Assert.Equal(LocalUserId, Assert.Single(_users.Calls).Args[0]);
    }

    /// <summary>
    /// A source item with no path builds no row and looks nothing up.
    /// True: a virtual item with no file is not tracked.
    /// False: a pathless item would be matched against the library root.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildRecord_NoSourcePath_ReturnsNull(string? path)
    {
        HoldUserAndItem();

        var row = _service.BuildRecord(Users(), Library(), SourceItem(path), "source-item");

        Assert.Null(row);
        Assert.Empty(_users.Calls);
        Assert.Empty(_items.Calls);
    }

    /// <summary>
    /// An item the Library Filter leaves out builds no row, and one it lets through does.
    /// True: filtered titles have no history rows, so the scan prunes any they had.
    /// False: history would sync for titles the operator filtered out.
    /// </summary>
    [Theory]
    [InlineData(LibraryFilterMode.Blacklist, "/media/movies/Film", false)]
    [InlineData(LibraryFilterMode.Blacklist, "/media/movies/Film2", true)]
    [InlineData(LibraryFilterMode.Whitelist, "/media/movies/Film", true)]
    [InlineData(LibraryFilterMode.Whitelist, "/media/movies/Other", false)]
    [InlineData(LibraryFilterMode.AllowAll, "/media/movies/Film", true)]
    public void BuildRecord_LibraryFilter_DecidesWhetherARowIsBuilt(LibraryFilterMode mode, string filteredPath, bool built)
    {
        HoldUserAndItem();
        var library = Library();
        library.FilterMode = mode;
        library.FilteredItems = new List<FilteredItem> { new() { Path = filteredPath } };

        var row = _service.BuildRecord(Users(), library, SourceItem(), "source-item");

        Assert.Equal(built, row is not null);
    }

    /// <summary>
    /// A filter mode with nothing listed leaves nothing out.
    /// True: choosing Whitelist before adding a path does not drop every history row.
    /// False: an empty whitelist would prune the whole library's history.
    /// </summary>
    [Fact]
    public void BuildRecord_FilterWithNoPaths_StillBuildsTheRow()
    {
        HoldUserAndItem();
        var library = Library();
        library.FilterMode = LibraryFilterMode.Whitelist;
        library.FilteredItems = null!;

        var row = _service.BuildRecord(Users(), library, SourceItem(), "source-item");

        Assert.NotNull(row);
    }

    /// <summary>
    /// With no local user, no row is built and no item is looked up.
    /// True: a mapping to a deleted user is skipped cheaply.
    /// False: every item of a dead mapping would cost a library lookup.
    /// </summary>
    [Fact]
    public void BuildRecord_LocalUserMissing_ReturnsNullBeforeTheItemLookup()
    {
        _items.Answer("FindByPath", _ => new Movie { Id = LocalItemId });

        var row = _service.BuildRecord(Users(), Library(), SourceItem(), "source-item");

        Assert.Null(row);
        Assert.Empty(_items.Calls);
    }

    /// <summary>
    /// With no local item at the translated path, no row is built, and the lookup asks for a file at that path.
    /// True: history for a title this server does not hold is not tracked.
    /// False: a row with no local item would error on every apply.
    /// </summary>
    [Fact]
    public void BuildRecord_NoLocalItem_ReturnsNullAfterAskingForTheTranslatedFile()
    {
        _users.Answer("GetUserById", _ => _user);

        var row = _service.BuildRecord(Users(), Library(), SourceItem(), "source-item");

        var call = Assert.Single(_items.Calls);
        Assert.Null(row);
        Assert.Equal("/local/movies/Film/film.mkv", call.Args[0]);
        Assert.Equal(false, call.Args[1]);
        Assert.Empty(_userData.Calls);
    }

    /// <summary>
    /// A matched item builds a row with both mappings' ids, the paths, the source's state, and a source state hash.
    /// True: the row says whose history of which item it is, on both servers.
    /// False: the apply step would write to the wrong user or item.
    /// </summary>
    [Fact]
    public void BuildRecord_Match_FillsIdentityPathsAndSourceState()
    {
        HoldUserAndItem();

        var row = _service.BuildRecord(Users(), Library(), SourceItem(), "source-item", negotiateWithSource: true)!;

        Assert.Equal(("source-user", LocalUserId.ToString("D"), "source-lib", "local-lib"), (row.SourceUserId, row.LocalUserId, row.SourceLibraryId, row.LocalLibraryId));
        Assert.Equal(("source-item", LocalItemId.ToString("N"), "Film"), (row.SourceItemId, row.LocalItemId, row.ItemName));
        Assert.Equal(("/media/movies/Film/film.mkv", "/local/movies/Film/film.mkv"), (row.SourcePath, row.LocalPath));
        Assert.Equal((true, 3, 100L, Played.UtcDateTime, true), (row.SourceIsPlayed, row.SourcePlayCount, row.SourcePlaybackPositionTicks, row.SourceLastPlayedDate, row.SourceIsFavorite));
        Assert.True(row.NegotiateWithSource);
        Assert.False(string.IsNullOrEmpty(row.SourceState.SourceHash));
    }

    /// <summary>
    /// The local side is this server's own history for that user and item, and the merged count is the larger of the two.
    /// True: a title watched more on either server keeps its higher count.
    /// False: the local state would be lost and the merge would always take the source.
    /// </summary>
    [Fact]
    public void BuildRecord_LocalHistory_IsReadAndMerged()
    {
        HoldUserAndItem();
        var lastPlayed = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _userData.Answer("GetUserData", _ => new UserItemData { Key = "k", Played = false, PlayCount = 7, PlaybackPositionTicks = 50, LastPlayedDate = lastPlayed, IsFavorite = false });
        HistorySyncItem row;

        using (HistorySyncMergeService.WithFields(HistoryFields.All))
        {
            row = _service.BuildRecord(Users(), Library(), SourceItem(), "source-item")!;
        }

        Assert.Equal((false, 7, 50L, lastPlayed, false), (row.LocalIsPlayed, row.LocalPlayCount, row.LocalPlaybackPositionTicks, row.LocalLastPlayedDate, row.LocalIsFavorite));
        Assert.Equal(7, row.MergedPlayCount);
        Assert.False(row.NegotiateWithSource);
    }

    /// <summary>
    /// A source item with no history, and a local item with none, leave both sides unknown.
    /// True: "never read" stays different from "not played".
    /// False: a title nobody touched would be stored as unplayed and overwrite real history later.
    /// </summary>
    [Fact]
    public void BuildRecord_NoHistoryOnEitherSide_LeavesBothSidesNull()
    {
        HoldUserAndItem();
        var source = SourceItem();
        source.UserData = null;

        var row = _service.BuildRecord(Users(), Library(), source, "source-item")!;

        Assert.Null(row.SourceIsPlayed);
        Assert.Null(row.SourcePlayCount);
        Assert.Null(row.SourceLastPlayedDate);
        Assert.Null(row.LocalIsPlayed);
        Assert.Null(row.LocalPlayCount);
    }

    /// <summary>
    /// A source item with no name is named after its file, and a mapping with no local library id stores an empty one.
    /// True: every row has something to show and a value the table accepts.
    /// False: a nameless row would list blank, or a null id would fail the write.
    /// </summary>
    [Fact]
    public void BuildRecord_NoNameAndNoLocalLibrary_FallsBackToFileNameAndEmptyId()
    {
        HoldUserAndItem();
        var library = Library();
        library.LocalLibraryId = null!;

        var row = _service.BuildRecord(Users(), library, SourceItem(name: null), "source-item")!;

        Assert.Equal("film", row.ItemName);
        Assert.Equal(string.Empty, row.LocalLibraryId);
    }

    /// <summary>
    /// A user finder passed in is used in place of the server lookup.
    /// True: a run that builds many rows looks each user up once.
    /// False: every row would cost a user lookup however the caller cached it.
    /// </summary>
    [Fact]
    public void BuildRecord_FinderGiven_IsUsedInPlaceOfTheLookup()
    {
        _items.Answer("FindByPath", _ => new Movie { Id = LocalItemId });
        var asked = new List<UserMapping>();
        var mapping = Users();

        var row = _service.BuildRecord(mapping, Library(), SourceItem(), "source-item", findUser: m =>
        {
            asked.Add(m);
            return _user;
        });

        Assert.NotNull(row);
        Assert.Same(mapping, Assert.Single(asked));
        Assert.Empty(_users.Calls);
        Assert.Same(_user, _userData.Calls.Single(c => c.Name == "GetUserData").Args[0]);
    }

    /// <summary>
    /// Each required argument is checked.
    /// True: the caller fails at once with the argument named.
    /// False: a scan would fail with a null reference and no cause.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void BuildRecord_NullArgument_Throws(int missing)
    {
        Assert.Throws<ArgumentNullException>(() => _service.BuildRecord(
            missing == 0 ? null! : Users(),
            missing == 1 ? null! : Library(),
            missing == 2 ? null! : SourceItem(),
            missing == 3 ? null! : "source-item"));
        Assert.Throws<ArgumentNullException>(() => _service.FindLocalUser(null!));
    }
}
