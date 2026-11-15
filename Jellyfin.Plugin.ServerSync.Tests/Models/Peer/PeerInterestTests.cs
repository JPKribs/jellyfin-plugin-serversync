using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Models.Peer;

/// <summary>
/// What this server tells a peer it wants is exactly its turned on modules, its enabled mappings, and its mapped users, and a hint is wanted only when it falls inside them.
/// </summary>
public sealed class PeerInterestTests
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static PluginConfiguration AllOn() => new()
    {
        EnableHistorySync = true,
        EnableMetadataSync = true,
        EnablePeopleSync = true,
        EnableContentSync = true,
        EnablePlaylistSync = true
    };

    private static SourceServer Entry() => new()
    {
        LibraryMappings = new List<LibraryMapping>
        {
            new() { SourceLibraryId = "shows", SourceRootPath = "/media/shows" },
            new() { SourceLibraryId = "movies", SourceRootPath = "/media/movies" }
        },
        UserMappings = new List<UserMapping>
        {
            new() { SourceUserId = Alice.ToString("D"), LocalUserId = Guid.NewGuid().ToString("N") }
        }
    };

    private static PeerInterest Interest(LibraryFilterMode mode = LibraryFilterMode.AllowAll, params string[] filtered) => new()
    {
        Kinds = new List<HintKind> { HintKind.History, HintKind.Metadata, HintKind.People, HintKind.Content, HintKind.Playlists },
        Libraries = new List<PeerInterestLibrary> { new() { Root = "/media/movies", FilterMode = mode, Filtered = filtered.ToList() } },
        Users = new List<string> { Alice.ToString("N") }
    };

    /// <summary>
    /// With every module off, no kind is listed.
    /// True: a peer sends nothing to a server that takes nothing.
    /// False: a peer would keep queueing hints this server refuses on arrival.
    /// </summary>
    [Fact]
    public void For_EveryModuleOff_ListsNoKinds()
    {
        var interest = PeerInterest.For(new PluginConfiguration(), Entry());

        Assert.Empty(interest.Kinds);
    }

    /// <summary>
    /// Libraries are listed by the peer's root folder in ordinal order, and a disabled mapping or one with no root is left out.
    /// True: the peer filters on the folders this server maps, in a stable order.
    /// False: a disabled library would still draw hints, or the revision would change with list order.
    /// </summary>
    [Fact]
    public void For_Mappings_ListsEnabledRootsInOrder()
    {
        var entry = Entry();
        entry.LibraryMappings.Add(new LibraryMapping { SourceRootPath = "/media/disabled", IsEnabled = false });
        entry.LibraryMappings.Add(new LibraryMapping { SourceRootPath = "  " });

        var interest = PeerInterest.For(AllOn(), entry);

        Assert.Equal(new[] { "/media/movies", "/media/shows" }, interest.Libraries.Select(l => l.Root).ToArray());
    }

    /// <summary>
    /// A filtered library lists its paths in ordinal order without blank entries, and an Allow All library lists none even when paths are stored.
    /// True: the peer applies the same filter this server would.
    /// False: leftover paths on an Allow All library would make the peer hold hints back.
    /// </summary>
    [Fact]
    public void For_FilteredLibraries_ListsPathsOnlyWhenTheFilterIsOn()
    {
        var entry = Entry();
        entry.LibraryMappings[0].FilteredItems = new List<FilteredItem> { new() { Path = "/media/shows/Leftover" } };
        entry.LibraryMappings[1].FilterMode = LibraryFilterMode.Blacklist;
        entry.LibraryMappings[1].FilteredItems = new List<FilteredItem> { new() { Path = "/media/movies/B" }, new() { Path = string.Empty }, new() { Path = "/media/movies/A" } };

        var interest = PeerInterest.For(AllOn(), entry);

        var movies = interest.Libraries.Single(l => l.Root == "/media/movies");
        var shows = interest.Libraries.Single(l => l.Root == "/media/shows");
        Assert.Equal(LibraryFilterMode.Blacklist, movies.FilterMode);
        Assert.Equal(new[] { "/media/movies/A", "/media/movies/B" }, movies.Filtered.ToArray());
        Assert.Empty(shows.Filtered);
    }

    /// <summary>
    /// Users are listed as 32 character hex ids, once each, in order, without disabled mappings or ids that are not Guids.
    /// True: the peer matches a user however this server's settings spelled the id.
    /// False: a dashed id would never match and that user's history hints would be dropped.
    /// </summary>
    [Fact]
    public void For_UserMappings_ListsDistinctHexIdsOfEnabledMappings()
    {
        var entry = Entry();
        entry.UserMappings.Add(new UserMapping { SourceUserId = Alice.ToString("N").ToUpperInvariant() });
        entry.UserMappings.Add(new UserMapping { SourceUserId = Bob.ToString("D"), IsEnabled = false });
        entry.UserMappings.Add(new UserMapping { SourceUserId = "not-a-guid" });
        entry.UserMappings.Add(new UserMapping { SourceUserId = string.Empty });

        var interest = PeerInterest.For(AllOn(), entry);

        Assert.Equal(new[] { Alice.ToString("N") }, interest.Users.ToArray());
    }

    /// <summary>
    /// The revision is 16 hex characters and does not change with the order mappings are stored in.
    /// True: a peer refetches the interest only when its content changed.
    /// False: reordering mappings in the settings would make every peer refetch.
    /// </summary>
    [Fact]
    public void For_SameMappingsInAnotherOrder_GivesTheSameRevision()
    {
        var entry = Entry();
        var reversed = Entry();
        reversed.LibraryMappings.Reverse();

        var first = PeerInterest.For(AllOn(), entry);
        var second = PeerInterest.For(AllOn(), reversed);

        Assert.Matches("^[0-9A-F]{16}$", first.Revision);
        Assert.Equal(first.Revision, second.Revision);
    }

    /// <summary>
    /// Turning a module off changes the revision.
    /// True: a peer learns that this server stopped taking a kind.
    /// False: the peer would keep its old copy and go on sending that kind.
    /// </summary>
    [Fact]
    public void For_ModuleTurnedOff_ChangesTheRevision()
    {
        var config = AllOn();
        var before = PeerInterest.For(config, Entry());
        config.EnablePeopleSync = false;

        var after = PeerInterest.For(config, Entry());

        Assert.NotEqual(before.Revision, after.Revision);
    }

    /// <summary>
    /// An entry with no mappings at all builds an empty interest.
    /// True: a freshly paired peer gets an answer that asks for nothing.
    /// False: pairing would fail until a mapping exists.
    /// </summary>
    [Fact]
    public void For_EntryWithNoMappings_BuildsEmptyLists()
    {
        var entry = new SourceServer { LibraryMappings = null!, UserMappings = null! };

        var interest = PeerInterest.For(AllOn(), entry);

        Assert.Empty(interest.Libraries);
        Assert.Empty(interest.Users);
        Assert.Equal(16, interest.Revision.Length);
    }

    /// <summary>
    /// Missing settings or a missing entry is refused.
    /// True: the caller fails at once with the argument named.
    /// False: the peer route would fail with a null reference and no cause.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void For_NullArgument_Throws(bool nullConfig)
    {
        Assert.Throws<ArgumentNullException>(() => PeerInterest.For(nullConfig ? null! : AllOn(), nullConfig ? Entry() : null!));
    }

    /// <summary>
    /// A playlist change is wanted for a mapped user only, whatever the path.
    /// True: a mapped user's playlists arrive and nobody else's do.
    /// False: an unmapped user's playlists would be sent to a server with nowhere to put them.
    /// </summary>
    [Fact]
    public void Wants_Playlists_FollowsTheUserNotThePath()
    {
        var interest = Interest();

        Assert.True(interest.Wants(HintKind.Playlists, null, Alice));
        Assert.False(interest.Wants(HintKind.Playlists, "/media/movies/a.mkv", Bob));
    }

    /// <summary>
    /// A kind this server did not list is never wanted, people included.
    /// True: a module that is off draws no hints.
    /// False: the peer would send hints for a module this server turned off.
    /// </summary>
    [Theory]
    [InlineData(HintKind.People)]
    [InlineData(HintKind.Users)]
    [InlineData(HintKind.Metadata)]
    public void Wants_KindNotListed_IsFalse(HintKind kind)
    {
        var interest = Interest();
        interest.Kinds = new List<HintKind> { HintKind.History };

        var wanted = interest.Wants(kind, "/media/movies/a.mkv", Alice);

        Assert.False(wanted);
    }

    /// <summary>
    /// An item change with no path, or an empty one, is not wanted.
    /// True: a change that cannot be placed in a library is held back.
    /// False: a pathless hint would be sent and fail to map on arrival every time.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Wants_ItemKindWithNoPath_IsFalse(string? path)
    {
        var interest = Interest();

        Assert.False(interest.Wants(HintKind.Metadata, path, Alice));
        Assert.False(interest.Wants(HintKind.History, path, Alice));
    }

    /// <summary>
    /// A sibling folder that shares the root's prefix is outside the library, and the root itself is inside it.
    /// True: only paths under the mapped folder draw hints.
    /// False: /media/movies2 would be treated as part of /media/movies.
    /// </summary>
    [Theory]
    [InlineData("/media/movies2/a.mkv", false)]
    [InlineData("/media/movies", true)]
    [InlineData("/media/movies/Film/a.mkv", true)]
    [InlineData("/elsewhere/a.mkv", false)]
    public void Wants_PathAgainstTheRoot_RespectsTheFolderBoundary(string path, bool expected)
    {
        var interest = Interest();

        var wanted = interest.Wants(HintKind.Content, path, Guid.Empty);

        Assert.Equal(expected, wanted);
    }

    /// <summary>
    /// A blacklist holds back the paths it lists and lets the rest through.
    /// True: a blocked folder draws no hints and its neighbors still do.
    /// False: a blacklist would act as a whitelist, or not at all.
    /// </summary>
    [Theory]
    [InlineData("/media/movies/Blocked/a.mkv", false)]
    [InlineData("/media/movies/Blocked2/a.mkv", true)]
    [InlineData("/media/movies/Other/a.mkv", true)]
    public void Wants_Blacklist_HoldsBackListedPathsOnly(string path, bool expected)
    {
        var interest = Interest(LibraryFilterMode.Blacklist, "/media/movies/Blocked");

        var wanted = interest.Wants(HintKind.Metadata, path, Guid.Empty);

        Assert.Equal(expected, wanted);
    }

    /// <summary>
    /// A whitelist with nothing listed lets every path of the library through.
    /// True: an empty filter does not silence a library.
    /// False: choosing Whitelist before adding a path would stop every hint.
    /// </summary>
    [Fact]
    public void Wants_WhitelistWithNoPaths_LetsEverythingThrough()
    {
        var interest = Interest(LibraryFilterMode.Whitelist);

        var wanted = interest.Wants(HintKind.Metadata, "/media/movies/Any/a.mkv", Guid.Empty);

        Assert.True(wanted);
    }

    /// <summary>
    /// When two libraries hold a path, the one with the longer root decides.
    /// True: a nested library's own filter applies to its items.
    /// False: the outer library's filter would hide or admit items of the inner one.
    /// </summary>
    [Fact]
    public void Wants_NestedLibraries_TheMostSpecificRootDecides()
    {
        var interest = Interest();
        interest.Libraries.Add(new PeerInterestLibrary { Root = "/media/movies/kids", FilterMode = LibraryFilterMode.Whitelist, Filtered = new List<string> { "/media/movies/kids/Wanted" } });

        Assert.False(interest.Wants(HintKind.Metadata, "/media/movies/kids/Other/a.mkv", Guid.Empty));
        Assert.True(interest.Wants(HintKind.Metadata, "/media/movies/kids/Wanted/a.mkv", Guid.Empty));
        Assert.True(interest.Wants(HintKind.Metadata, "/media/movies/Other/a.mkv", Guid.Empty));
    }

    /// <summary>
    /// A history change needs both a mapped user and a path inside a library.
    /// True: history arrives only where a user and an item can both be matched.
    /// False: history for an unmapped library or user would be sent and then dropped.
    /// </summary>
    [Fact]
    public void Wants_History_NeedsMappedUserAndMappedPath()
    {
        var interest = Interest();

        Assert.True(interest.Wants(HintKind.History, "/media/movies/a.mkv", Alice));
        Assert.False(interest.Wants(HintKind.History, "/media/shows/a.mkv", Alice));
        Assert.False(interest.Wants(HintKind.History, "/media/movies/a.mkv", Bob));
    }

    /// <summary>
    /// A user id stored in upper case still matches.
    /// True: an interest another build wrote in another case still works.
    /// False: a case difference in a stored id would silence that user's history.
    /// </summary>
    [Fact]
    public void Wants_UserIdInAnotherCase_StillMatches()
    {
        var interest = Interest();
        interest.Users = new List<string> { Alice.ToString("N").ToUpperInvariant() };

        var wanted = interest.Wants(HintKind.Playlists, null, Alice);

        Assert.True(wanted);
    }
}
