using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.ContentSync.Tables;

/// <summary>
/// A new source file is paired with an old row only when the row's file had the same size, or is the same episode, or the same film and version.
/// </summary>
public sealed class RenameMatcherTests
{
    private static SyncItem Row(string sourcePath, long size = 100) => new() { SourceItemId = Guid.NewGuid().ToString("N"), SourcePath = sourcePath, SourceSize = size };

    private static BaseItemDto Offered(BaseItemDto_Type type, string path, int? season = null, int? episode = null, string? imdb = null)
    {
        var item = new BaseItemDto { Type = type, Path = path, ParentIndexNumber = season, IndexNumber = episode };
        if (imdb is not null)
        {
            item.ProviderIds = new BaseItemDto_ProviderIds { AdditionalData = new Dictionary<string, object> { ["Imdb"] = imdb } };
        }

        return item;
    }

    /// <summary>
    /// A file with the same size in the same folder fits, whatever its type.
    /// True: a pure rename moves the file here without a download.
    /// False: a renamed file downloads again and the old one is deleted.
    /// </summary>
    [Fact]
    public void Fits_SameSize_IsARename()
    {
        Assert.True(RenameMatcher.Fits(Offered(BaseItemDto_Type.Video, "/m/Clips/b.mp4"), 100, Row("/m/Clips/a.mp4"), null));
    }

    /// <summary>
    /// An episode with a new size fits only the row of the same season and episode.
    /// True: a whole season renamed and re-encoded at once pairs each episode with its own row.
    /// False: an episode replaces another episode's file.
    /// </summary>
    [Theory]
    [InlineData(1, 2, true)]
    [InlineData(1, 3, false)]
    [InlineData(2, 2, false)]
    public void Fits_Episode_MatchesBySeasonAndNumber(int season, int number, bool expected)
    {
        var local = new Episode { ParentIndexNumber = 1, IndexNumber = 2 };

        var fits = RenameMatcher.Fits(Offered(BaseItemDto_Type.Episode, "/m/Show/Season 01/Show - S01E02.mkv", season, number), 999, Row("/m/Show/Season 01/Show - S01E02 - Old.mp4"), local);

        Assert.Equal(expected, fits);
    }

    /// <summary>
    /// A film with a new size fits when a provider id matches this server's item and the version label is the same.
    /// True: a film in a new container replaces its own version, and a 4K file never replaces the 1080p one.
    /// False: one version of a film overwrites another, or a film that was not identified replaces a different one.
    /// </summary>
    [Theory]
    [InlineData("/m/Film (2020)/Film (2020).mkv", "tt1", true)]
    [InlineData("/m/Film (2020)/Film (2020) - 4K.mkv", "tt1", false)]
    [InlineData("/m/Film (2020)/Film (2020).mkv", "tt2", false)]
    [InlineData("/m/Film (2020)/Film (2020).mkv", null, false)]
    [InlineData("/m/Film (2020) [imdbid-tt1]/Film (2020) [imdbid-tt1].mkv", null, true)]
    public void Fits_Film_MatchesByProviderAndVersion(string newPath, string? imdb, bool expected)
    {
        var local = new Movie { ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Imdb"] = "tt1" } };
        var oldPath = newPath.Contains("[imdbid", StringComparison.Ordinal) ? "/m/Film (2020) [imdbid-tt1]/Film (2020) [imdbid-tt1].mp4" : "/m/Film (2020)/Film (2020).mp4";

        Assert.Equal(expected, RenameMatcher.Fits(Offered(BaseItemDto_Type.Movie, newPath, imdb: imdb), 999, Row(oldPath), local));
    }

    /// <summary>
    /// The label is the text after the folder name and a dash, and a file named otherwise has none.
    /// True: versions are told apart by the name Jellyfin reads them from.
    /// False: two versions of one film pair with each other.
    /// </summary>
    [Theory]
    [InlineData("/m/Film (2020)/Film (2020) - 4K.mkv", "4K")]
    [InlineData("/m/Film (2020)/Film (2020) - Director's Cut.mp4", "Director's Cut")]
    [InlineData("/m/Film (2020)/Film (2020).mkv", "")]
    [InlineData("/m/Film (2020)/film.2020.1080p.mkv", "")]
    [InlineData(null, "")]
    public void VersionLabel_IsTheTextAfterTheFolderName(string? path, string expected)
    {
        Assert.Equal(expected, RenameMatcher.VersionLabel(path));
    }

    /// <summary>
    /// Only exactly one fitting row is a match.
    /// True: an ambiguous rename arrives as a new file, as it did before.
    /// False: a guess moves the wrong file.
    /// </summary>
    [Fact]
    public void Single_TwoFit_IsNoMatch()
    {
        var a = Row("/m/a.mp4");
        var b = Row("/m/b.mp4");

        Assert.Same(a, RenameMatcher.Single(new[] { a }));
        Assert.Null(RenameMatcher.Single(new[] { a, b }));
        Assert.Null(RenameMatcher.Single(Array.Empty<SyncItem>()));
    }
}
