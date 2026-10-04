using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Queue;

public class HintActivityLogTests
{
    /// <summary>
    /// An episode is titled by its series and number, and its line adds the episode's own name.
    /// True: "Synced The Show S01E02" and "Metadata for The Show S01E02 - Pilot was updated from a".
    /// False: an entry reads "Synced Pilot", which names nothing on a server with many shows.
    /// </summary>
    [Fact]
    public void Episode_IsTitledBySeriesAndNumber()
    {
        var episode = new Episode { Name = "Pilot", SeriesName = "The Show", ParentIndexNumber = 1, IndexNumber = 2 };
        Assert.Equal("The Show S01E02", HintActivityLog.TitleOf(episode));
        Assert.Equal("The Show S01E02 - Pilot", HintActivityLog.LineOf(episode));
    }

    /// <summary>
    /// A season is titled by its series, since "Season 1" alone names nothing.
    /// True: "Synced The Show Season 1".
    /// False: "Synced Season 1".
    /// </summary>
    [Fact]
    public void Season_IsTitledBySeries()
    {
        var season = new Season { Name = "Season 1", SeriesName = "The Show" };
        Assert.Equal("The Show Season 1", HintActivityLog.TitleOf(season));
        Assert.Equal("The Show Season 1", HintActivityLog.LineOf(season));
    }

    /// <summary>
    /// Anything else goes by its name, and the subject wraps it by kind without a full stop.
    /// True: "Metadata for Only A (2021)".
    /// False: a film is renamed or the entry ends in a full stop like nothing else in the log.
    /// </summary>
    [Fact]
    public void Movie_GoesByName()
    {
        var movie = new Movie { Name = "Only A (2021)" };
        Assert.Equal("Only A (2021)", HintActivityLog.TitleOf(movie));
        Assert.Equal("Metadata for Only A (2021)", HintActivityLog.SubjectOf(HintKind.Metadata, HintActivityLog.LineOf(movie), null));
        Assert.Equal("Watch history for Only A (2021) (viewer)", HintActivityLog.SubjectOf(HintKind.History, HintActivityLog.LineOf(movie), "viewer"));
    }
}
