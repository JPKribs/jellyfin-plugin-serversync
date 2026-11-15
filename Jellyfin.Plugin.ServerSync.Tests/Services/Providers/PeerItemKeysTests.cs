using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Services.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Providers;

/// <summary>
/// The keys must carry everything another server can match an item by, taken from lookup info or from a local item, and nothing invented.
/// </summary>
public sealed class PeerItemKeysTests
{
    /// <summary>
    /// Lookup info for a film gives its path, name, ids, and numbers, with no series ids.
    /// True: the source is asked with every key the film has.
    /// False: a key would be dropped and the film matched by less than it could be.
    /// </summary>
    [Fact]
    public void FromInfo_Movie_CarriesItsOwnKeys()
    {
        var info = new MovieInfo { Path = "/m/film.mkv", Name = "Film", IndexNumber = 1, ParentIndexNumber = 2 };
        info.ProviderIds["Imdb"] = "tt1";

        var keys = PeerItemKeys.FromInfo("Movie", info);

        Assert.Equal("Movie", keys.ItemType);
        Assert.Equal("/m/film.mkv", keys.LocalPath);
        Assert.Equal("Film", keys.Name);
        Assert.Equal("tt1", keys.ProviderIds!["Imdb"]);
        Assert.Null(keys.SeriesProviderIds);
        Assert.Equal(1, keys.IndexNumber);
        Assert.Equal(2, keys.ParentIndexNumber);
    }

    /// <summary>
    /// Lookup info for an episode or a season also gives the ids of its series.
    /// True: an episode with no ids of its own is matched under its series.
    /// False: episodes could only ever match by path.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FromInfo_EpisodeOrSeason_CarriesTheSeriesIds(bool episode)
    {
        var seriesIds = new Dictionary<string, string> { ["Tvdb"] = "42" };
        ItemLookupInfo info = episode
            ? new EpisodeInfo { SeriesProviderIds = seriesIds, IndexNumber = 5, ParentIndexNumber = 2 }
            : new SeasonInfo { SeriesProviderIds = seriesIds, IndexNumber = 2 };

        var keys = PeerItemKeys.FromInfo(episode ? "Episode" : "Season", info);

        Assert.Same(seriesIds, keys.SeriesProviderIds);
        Assert.Equal(episode ? 5 : 2, keys.IndexNumber);
        Assert.Equal(episode ? 2 : null, keys.ParentIndexNumber);
    }

    /// <summary>
    /// A local item gives its own type name, path, name, and ids.
    /// True: an image search asks the source about the same item a metadata search would.
    /// False: images would be looked up under another type or path than the metadata was.
    /// </summary>
    [Fact]
    public void FromItem_Movie_CarriesItsTypeAndKeys()
    {
        var movie = new Movie { Path = "/m/film.mkv", Name = "Film", IndexNumber = 7 };
        movie.ProviderIds["Tmdb"] = "9";

        var keys = PeerItemKeys.FromItem(movie);

        Assert.Equal("Movie", keys.ItemType);
        Assert.Equal("/m/film.mkv", keys.LocalPath);
        Assert.Equal("Film", keys.Name);
        Assert.Equal("9", keys.ProviderIds!["Tmdb"]);
        Assert.Null(keys.SeriesProviderIds);
        Assert.Equal(7, keys.IndexNumber);
        Assert.Null(keys.ParentIndexNumber);
    }

    /// <summary>
    /// An item with a path is described by it, and one without by its type and name.
    /// True: a "no match" log line names the file, or the person.
    /// False: a person, who has no path, would be logged as an empty string.
    /// </summary>
    [Theory]
    [InlineData("/m/film.mkv", "Film", "/m/film.mkv")]
    [InlineData(null, "Ada Lovelace", "Person Ada Lovelace")]
    [InlineData("", "Ada Lovelace", "Person Ada Lovelace")]
    [InlineData(null, null, "Person ")]
    public void Describe_WithAndWithoutAPath_NamesTheItem(string? path, string? name, string expected)
    {
        var keys = new PeerItemKeys("Person", path, name, null, null, null, null);

        Assert.Equal(expected, keys.Describe());
    }

    /// <summary>
    /// Missing lookup info or a missing item is refused.
    /// True: a bad call fails at once with the argument's name.
    /// False: the provider would fail later with a null reference inside a refresh.
    /// </summary>
    [Fact]
    public void FromInfoAndFromItem_Null_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => PeerItemKeys.FromInfo("Movie", null!));
        Assert.Throws<ArgumentNullException>(() => PeerItemKeys.FromItem(null!));
    }
}
