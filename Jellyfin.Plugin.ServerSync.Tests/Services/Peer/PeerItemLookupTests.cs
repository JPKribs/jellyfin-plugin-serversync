using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Peer;

/// <summary>
/// The lookup must find the item a peer means by path first, then ids, then series and index, then name, and must bound the ids a peer may send.
/// </summary>
public sealed class PeerItemLookupTests
{
    private readonly ILibraryManager _libraryManager;
    private readonly InterfaceStub _library;
    private readonly ILocalItems _localItems;
    private readonly InterfaceStub _paths;
    private readonly List<InternalItemsQuery> _queries = new();
    private readonly Dictionary<BaseItemKind, BaseItem> _byKind = new();
    private readonly PeerItemLookup _lookup;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerItemLookupTests"/> class over a library that answers each query with the item set for its kind.
    /// </summary>
    public PeerItemLookupTests()
    {
        (_libraryManager, _library) = InterfaceStub.Create<ILibraryManager>();
        (_localItems, _paths) = InterfaceStub.Create<ILocalItems>();
        _library.Answer(nameof(ILibraryManager.GetItemList), args =>
        {
            var query = (InternalItemsQuery)args[0]!;
            _queries.Add(query);
            return _byKind.TryGetValue(query.IncludeItemTypes.Single(), out var item) ? new List<BaseItem> { item } : new List<BaseItem>();
        });
        _lookup = new PeerItemLookup(_libraryManager, _localItems);
    }

    /// <summary>
    /// Missing or empty ids clean to null.
    /// True: a request with nothing usable skips the id query.
    /// False: an empty id set would be sent to the library as a filter that matches nothing, or everything.
    /// </summary>
    [Fact]
    public void CleanIds_NullOrNothingUsable_IsNull()
    {
        Assert.Null(PeerItemLookup.CleanIds(null));
        Assert.Null(PeerItemLookup.CleanIds(new Dictionary<string, string>()));
        Assert.Null(PeerItemLookup.CleanIds(new Dictionary<string, string> { [" "] = "tt1", ["Imdb"] = " ", [string.Empty] = string.Empty }));
    }

    /// <summary>
    /// A key over 64 characters or a value over 128 is dropped, and one at the limit is kept.
    /// True: a peer cannot push oversized text into a library query.
    /// False: a hostile peer could send megabytes as a provider id.
    /// </summary>
    [Fact]
    public void CleanIds_KeyOrValueOverTheLimit_IsDropped()
    {
        var ids = new Dictionary<string, string>
        {
            [new string('k', 64)] = new string('v', 128),
            [new string('k', 65)] = "ok",
            ["Tmdb"] = new string('v', 129),
            ["Imdb"] = "tt1"
        };

        var clean = PeerItemLookup.CleanIds(ids);

        Assert.Equal(new[] { "Imdb", new string('k', 64) }, clean!.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>
    /// No more than the first 16 ids are kept, and the kept ids are read in any case.
    /// True: a request with hundreds of ids costs one bounded query.
    /// False: a peer could make every lookup an unbounded filter.
    /// </summary>
    [Theory]
    [InlineData(16, 16)]
    [InlineData(17, 16)]
    [InlineData(500, 16)]
    public void CleanIds_ManyIds_KeepsAtMostSixteen(int sent, int expected)
    {
        var ids = Enumerable.Range(0, sent).ToDictionary(i => "Provider" + i, i => "id" + i);

        var clean = PeerItemLookup.CleanIds(ids);

        Assert.Equal(expected, clean!.Count);
        Assert.Equal(16, PeerItemLookup.MaxProviderIds);
        Assert.Equal("id0", clean["PROVIDER0"]);
    }

    /// <summary>
    /// An item at the asked path is the answer, with its id without dashes and its type, and nothing else is queried.
    /// True: the exact file a peer names is matched first.
    /// False: a film sharing provider ids with another would be matched to the wrong file.
    /// </summary>
    [Theory]
    [InlineData("Movie")]
    [InlineData(null)]
    [InlineData("")]
    public void Find_ItemAtThePath_IsTheAnswer(string? itemType)
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        _paths.Answer(nameof(ILocalItems.FindByPath), args => (string)args[0]! == "/local/film.mkv" ? movie : null);
        _byKind[BaseItemKind.Movie] = new Movie { Id = Guid.NewGuid() };

        var found = _lookup.Find(new PeerLookupRequest { Path = "/local/film.mkv", ItemType = itemType, ProviderIds = new() { ["Imdb"] = "tt1" } });

        Assert.True(found.Found);
        Assert.Equal(movie.Id.ToString("N"), found.ItemId);
        Assert.Equal("Movie", found.ItemType);
        Assert.Empty(_queries);
    }

    /// <summary>
    /// An item at the path that is of another type than asked is not the answer.
    /// True: a series folder is never returned for an episode's path.
    /// False: metadata for one kind of item would be written onto another.
    /// </summary>
    [Fact]
    public void Find_ItemAtThePathOfAnotherType_IsNotTheAnswer()
    {
        _paths.Answer(nameof(ILocalItems.FindByPath), _ => new Movie { Id = Guid.NewGuid() });

        var found = _lookup.Find(new PeerLookupRequest { Path = "/local/film.mkv", ItemType = "Episode" });

        Assert.False(found.Found);
        Assert.Null(found.ItemId);
        Assert.Null(found.ItemType);
    }

    /// <summary>
    /// With no item at the path, the item is found by any of its cleaned provider ids among items of its kind.
    /// True: the same film under a different file name is still matched.
    /// False: a renamed file would never match.
    /// </summary>
    [Fact]
    public void Find_NoItemAtThePath_FallsBackToProviderIds()
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        _byKind[BaseItemKind.Movie] = movie;

        var found = _lookup.Find(new PeerLookupRequest { Path = "/elsewhere/film.mkv", ItemType = "Movie", ProviderIds = new() { ["Imdb"] = "tt1", [" "] = "junk" } });

        Assert.Equal(movie.Id.ToString("N"), found.ItemId);
        var query = Assert.Single(_queries);
        Assert.Equal(new[] { BaseItemKind.Movie }, query.IncludeItemTypes);
        Assert.Equal(new Dictionary<string, string> { ["Imdb"] = "tt1" }, query.HasAnyProviderId);
        Assert.True(query.Recursive);
        Assert.Equal(1, query.Limit);
    }

    /// <summary>
    /// A type the library does not know is never queried by ids.
    /// True: a peer cannot make this server run a query for a made up kind.
    /// False: a bad type name would throw or match items of every kind.
    /// </summary>
    [Theory]
    [InlineData("NotAKind")]
    [InlineData(null)]
    [InlineData("")]
    public void Find_UnknownType_IsNotQueried(string? itemType)
    {
        var found = _lookup.Find(new PeerLookupRequest { ItemType = itemType, ProviderIds = new() { ["Imdb"] = "tt1" }, Name = "Film" });

        Assert.False(found.Found);
        Assert.Empty(_queries);
    }

    /// <summary>
    /// An episode with no ids of its own is found under its series by season and episode number.
    /// True: episodes, which rarely carry provider ids, still match.
    /// False: no episode without ids could ever be filled from another server.
    /// </summary>
    [Fact]
    public void Find_Episode_IsFoundUnderItsSeriesByIndex()
    {
        var series = new Series { Id = Guid.NewGuid() };
        var episode = new Episode { Id = Guid.NewGuid() };
        _byKind[BaseItemKind.Series] = series;
        _byKind[BaseItemKind.Episode] = episode;

        var found = _lookup.Find(new PeerLookupRequest { ItemType = "Episode", SeriesProviderIds = new() { ["Tvdb"] = "42" }, IndexNumber = 5, ParentIndexNumber = 2 });

        Assert.Equal(episode.Id.ToString("N"), found.ItemId);
        Assert.Equal("Episode", found.ItemType);
        Assert.Equal(2, _queries.Count);
        Assert.Equal(new Dictionary<string, string> { ["Tvdb"] = "42" }, _queries[0].HasAnyProviderId);
        Assert.Equal(new[] { series.Id }, _queries[1].AncestorIds);
        Assert.Equal(5, _queries[1].IndexNumber);
        Assert.Equal(2, _queries[1].ParentIndexNumber);
    }

    /// <summary>
    /// A season is found under its series by its own number alone.
    /// True: season 2 matches season 2.
    /// False: a season would be looked up with an episode's parent number and never match.
    /// </summary>
    [Fact]
    public void Find_Season_IsFoundUnderItsSeriesByItsNumber()
    {
        var series = new Series { Id = Guid.NewGuid() };
        var season = new Season { Id = Guid.NewGuid() };
        _byKind[BaseItemKind.Series] = series;
        _byKind[BaseItemKind.Season] = season;

        var found = _lookup.Find(new PeerLookupRequest { ItemType = "Season", SeriesProviderIds = new() { ["Tvdb"] = "42" }, IndexNumber = 2, ParentIndexNumber = 9 });

        Assert.Equal(season.Id.ToString("N"), found.ItemId);
        Assert.Equal(new[] { BaseItemKind.Season }, _queries[1].IncludeItemTypes);
        Assert.Equal(2, _queries[1].IndexNumber);
        Assert.Null(_queries[1].ParentIndexNumber);
    }

    /// <summary>
    /// An episode with no number, or whose series is not here, is not found.
    /// True: an episode is never matched to a guess.
    /// False: the first episode of any series could be returned for one that cannot be placed.
    /// </summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData(5, false)]
    public void Find_EpisodeThatCannotBePlaced_IsNotFound(int? indexNumber, bool seriesIsHere)
    {
        if (seriesIsHere)
        {
            _byKind[BaseItemKind.Series] = new Series { Id = Guid.NewGuid() };
        }

        _byKind[BaseItemKind.Episode] = new Episode { Id = Guid.NewGuid() };

        var found = _lookup.Find(new PeerLookupRequest { ItemType = "Episode", SeriesProviderIds = new() { ["Tvdb"] = "42" }, IndexNumber = indexNumber, Name = "Pilot" });

        Assert.False(found.Found);
        Assert.DoesNotContain(_queries, q => q.IncludeItemTypes.Single() == BaseItemKind.Episode);
    }

    /// <summary>
    /// A person is found by name through the people lookup, and an artist or a collection by a name query.
    /// True: the kinds that have no path and often no ids still match.
    /// False: no person, artist, or collection could be filled from another server.
    /// </summary>
    [Fact]
    public void Find_PersonArtistOrCollection_IsFoundByName()
    {
        var person = new Person { Id = Guid.NewGuid() };
        var artist = new MusicArtist { Id = Guid.NewGuid() };
        var boxSet = new BoxSet { Id = Guid.NewGuid() };
        _library.Answer(nameof(ILibraryManager.GetPerson), args => (string)args[0]! == "Ada Lovelace" ? person : null);
        _byKind[BaseItemKind.MusicArtist] = artist;
        _byKind[BaseItemKind.BoxSet] = boxSet;

        var foundPerson = _lookup.Find(new PeerLookupRequest { ItemType = "Person", Name = "Ada Lovelace" });
        var foundArtist = _lookup.Find(new PeerLookupRequest { ItemType = "MusicArtist", Name = "The Band" });
        var foundBoxSet = _lookup.Find(new PeerLookupRequest { ItemType = "BoxSet", Name = "Trilogy" });

        Assert.Equal(person.Id.ToString("N"), foundPerson.ItemId);
        Assert.Equal(artist.Id.ToString("N"), foundArtist.ItemId);
        Assert.Equal(boxSet.Id.ToString("N"), foundBoxSet.ItemId);
        Assert.Equal(new[] { "The Band", "Trilogy" }, _queries.Select(q => q.Name));
    }

    /// <summary>
    /// A film is never found by name alone, and a blank name finds nothing.
    /// True: two films that share a title are never mixed up.
    /// False: a remake would take the original's metadata.
    /// </summary>
    [Theory]
    [InlineData("Movie", "Alien")]
    [InlineData("Person", " ")]
    [InlineData("Person", null)]
    public void Find_NameThatMayNotMatch_FindsNothing(string itemType, string? name)
    {
        _byKind[BaseItemKind.Movie] = new Movie { Id = Guid.NewGuid() };
        _library.Answer(nameof(ILibraryManager.GetPerson), _ => new Person { Id = Guid.NewGuid() });

        var found = _lookup.Find(new PeerLookupRequest { ItemType = itemType, Name = name });

        Assert.False(found.Found);
        Assert.Empty(_queries);
    }

    /// <summary>
    /// A missing request or dependency is refused.
    /// True: a bad call fails at once.
    /// False: the peer route would answer a null reference as a server error.
    /// </summary>
    [Fact]
    public void ConstructorAndFind_MissingArgument_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new PeerItemLookup(null!, _localItems));
        Assert.Throws<ArgumentNullException>(() => new PeerItemLookup(_libraryManager, null!));
        Assert.Throws<ArgumentNullException>(() => _lookup.Find(null!));
    }
}
