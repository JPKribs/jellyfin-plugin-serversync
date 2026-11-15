using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Sdk.Generated.Models;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// A link's .nfo must describe the source item, be told apart by its marker, and stay gone once the user removes it.
/// </summary>
public sealed class LinkNfoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "serversync-linknfo-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkNfoTests"/> class.
    /// </summary>
    public LinkNfoTests() => Directory.CreateDirectory(_dir);

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// The .nfo sits beside the link under the same name.
    /// True: Jellyfin pairs the .nfo with the link.
    /// False: the metadata would be written where Jellyfin never looks.
    /// </summary>
    /// <param name="link">The link's path.</param>
    /// <param name="expected">The .nfo's path.</param>
    [Theory]
    [InlineData("/media/Film (2020)/Film (2020).strm", "/media/Film (2020)/Film (2020).nfo")]
    [InlineData("/media/Show.S01/E01.strm", "/media/Show.S01/E01.nfo")]
    public void PathFor_Link_SwapsTheExtension(string link, string expected)
    {
        var path = LinkNfo.PathFor(link);

        Assert.Equal(expected, path);
    }

    /// <summary>
    /// An .nfo is this plugin's when the marker is in its first four lines, and not when it is missing, lower down, or the file is gone.
    /// True: a user who deletes the marker line keeps their edits.
    /// False: a user's own .nfo would be overwritten, or the plugin's never refreshed.
    /// </summary>
    /// <param name="linesBefore">How many lines come before the marker, or -1 for no marker, or -2 for no file.</param>
    /// <param name="expected">Whether the file is this plugin's.</param>
    [Theory]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(-1, false)]
    [InlineData(-2, false)]
    public void IsOurs_NeedsTheMarkerNearTheTop(int linesBefore, bool expected)
    {
        var path = Path.Combine(_dir, "Film.nfo");
        if (linesBefore > -2)
        {
            var lines = Enumerable.Repeat("<line/>", Math.Max(0, linesBefore)).ToList();
            lines.Add(linesBefore >= 0 ? "<!-- " + LinkNfo.Marker + " -->" : "<movie/>");
            File.WriteAllLines(path, lines);
        }

        var ours = LinkNfo.IsOurs(path);

        Assert.Equal(expected, ours);
    }

    /// <summary>
    /// Recording adds the .nfo's name to the row's companion files once and keeps what was there.
    /// True: the row remembers the .nfo without losing its subtitles.
    /// False: companions would be dropped, or the name added again on every refresh.
    /// </summary>
    [Fact]
    public void Record_AddsTheNameOnce_AndKeepsCompanions()
    {
        var row = new SyncItem { LocalPath = Path.Combine(_dir, "Film.strm"), CompanionFiles = "Film.en.srt" };

        LinkNfo.Record(row);
        LinkNfo.Record(row);

        Assert.Equal("Film.en.srt,Film.nfo", row.CompanionFiles);
        Assert.True(LinkNfo.Recorded(row));
    }

    /// <summary>
    /// The row's record is read whatever the name's case and spacing, and a row with other companions records none.
    /// True: a record written on one file system is still read on another.
    /// False: a recorded .nfo would be written again after the user removed it.
    /// </summary>
    /// <param name="companions">The row's companion files.</param>
    /// <param name="expected">Whether the .nfo is recorded.</param>
    [Theory]
    [InlineData("film.NFO", true)]
    [InlineData("Film.en.srt , Film.nfo ", true)]
    [InlineData("Film.en.srt", false)]
    [InlineData("Other.nfo", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Recorded_ReadsTheCompanionNames(string? companions, bool expected)
    {
        var row = new SyncItem { LocalPath = Path.Combine(_dir, "Film.strm"), CompanionFiles = companions };

        var recorded = LinkNfo.Recorded(row);

        Assert.Equal(expected, recorded);
    }

    /// <summary>
    /// A row with no local path records nothing, and recording it changes nothing.
    /// True: a row not yet placed here is left alone.
    /// False: a null path would throw during a refresh.
    /// </summary>
    [Fact]
    public void Record_NoLocalPath_ChangesNothing()
    {
        var row = new SyncItem { LocalPath = null, CompanionFiles = "x.nfo" };

        LinkNfo.Record(row);

        Assert.Equal("x.nfo", row.CompanionFiles);
        Assert.False(LinkNfo.Recorded(row));
        Assert.False(LinkNfo.RemovedByUser(row));
    }

    /// <summary>
    /// A recorded .nfo that is gone was removed by the user, and one still there, or never recorded, was not.
    /// True: a user hands the metadata back to Jellyfin by deleting the file once.
    /// False: the .nfo would come back on every run, or a missing one would never be written at all.
    /// </summary>
    /// <param name="recorded">Whether the row records the .nfo.</param>
    /// <param name="exists">Whether the .nfo is on disk.</param>
    /// <param name="expected">Whether the user removed it.</param>
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void RemovedByUser_RecordedAndGone(bool recorded, bool exists, bool expected)
    {
        var row = new SyncItem { LocalPath = Path.Combine(_dir, "Film.strm"), CompanionFiles = recorded ? "Film.nfo" : null };
        if (exists)
        {
            File.WriteAllText(Path.Combine(_dir, "Film.nfo"), "<movie/>");
        }

        var removed = LinkNfo.RemovedByUser(row);

        Assert.Equal(expected, removed);
    }

    /// <summary>
    /// The row helpers and the builders refuse a missing row or item.
    /// True: the mistake is named where it was made.
    /// False: a null would fail deep inside the XML writer.
    /// </summary>
    [Fact]
    public void Members_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => LinkNfo.Recorded(null!));
        Assert.Throws<ArgumentNullException>(() => LinkNfo.Record(null!));
        Assert.Throws<ArgumentNullException>(() => LinkNfo.RemovedByUser(null!));
        Assert.Throws<ArgumentNullException>(() => LinkNfo.Build(null!));
        Assert.Throws<ArgumentNullException>(() => LinkNfo.Write(Path.Combine(_dir, "Film.strm"), null!));
    }

    /// <summary>
    /// The root element follows the item's type, with movie for anything that is not an episode or a music video.
    /// True: Jellyfin's reader for the item's type accepts the file.
    /// False: an episode written as a movie would be read as the wrong kind.
    /// </summary>
    /// <param name="type">The source item's type.</param>
    /// <param name="expected">The root element's name.</param>
    [Theory]
    [InlineData(BaseItemDto_Type.Movie, "movie")]
    [InlineData(BaseItemDto_Type.Episode, "episodedetails")]
    [InlineData(BaseItemDto_Type.MusicVideo, "musicvideo")]
    [InlineData(BaseItemDto_Type.Video, "movie")]
    [InlineData(BaseItemDto_Type.Trailer, "movie")]
    public void Build_RootFollowsTheType(BaseItemDto_Type type, string expected)
    {
        var item = new BaseItemDto { Type = type, Name = "Title" };

        var root = XDocument.Parse(LinkNfo.Build(item)).Root!;

        Assert.Equal(expected, root.Name.LocalName);
        Assert.Equal("Title", root.Element("title")!.Value);
    }

    /// <summary>
    /// An item with nothing but a type gives a root with the marker comment and no empty elements.
    /// True: missing details are left for Jellyfin to fill, not written as blanks.
    /// False: empty titles and years would override what Jellyfin could find.
    /// </summary>
    [Fact]
    public void Build_EmptyItem_WritesOnlyTheMarker()
    {
        var item = new BaseItemDto { Type = BaseItemDto_Type.Movie, Name = "   ", RunTimeTicks = 0 };

        var text = LinkNfo.Build(item);

        var document = XDocument.Parse(text);
        Assert.Empty(document.Root!.Elements());
        Assert.Contains(LinkNfo.Marker, document.Nodes().OfType<XComment>().Single().Value, StringComparison.Ordinal);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The runtime is written in whole minutes, rounded.
    /// True: the .nfo holds the runtime in the unit Jellyfin reads.
    /// False: ticks written as minutes would give a film a runtime of centuries.
    /// </summary>
    /// <param name="seconds">The item's runtime in seconds.</param>
    /// <param name="expected">The minutes written.</param>
    [Theory]
    [InlineData(600, "10")]
    [InlineData(6060, "101")]
    [InlineData(100, "2")]
    [InlineData(20, "0")]
    public void Build_Runtime_IsWholeMinutes(int seconds, string expected)
    {
        var item = new BaseItemDto { Type = BaseItemDto_Type.Movie, RunTimeTicks = TimeSpan.FromSeconds(seconds).Ticks };

        var root = XDocument.Parse(LinkNfo.Build(item)).Root!;

        Assert.Equal(expected, root.Element("runtime")!.Value);
    }

    /// <summary>
    /// Ids are written in name order with their names as the source spells them, the first marked default, and empty ones left out.
    /// True: Jellyfin holds each id once, under the name the source used.
    /// False: an id would be held twice under two spellings, or an empty id would blank a real one.
    /// </summary>
    [Fact]
    public void Build_ProviderIds_AreOrderedAndSkipEmpty()
    {
        var item = new BaseItemDto
        {
            Type = BaseItemDto_Type.Movie,
            ProviderIds = new BaseItemDto_ProviderIds { AdditionalData = new Dictionary<string, object> { ["Tmdb"] = "587807", ["Empty"] = string.Empty, ["Imdb"] = "tt1361336", ["Null"] = null! } }
        };

        var ids = XDocument.Parse(LinkNfo.Build(item)).Root!.Elements("uniqueid").ToList();

        Assert.Equal(new[] { "Imdb", "Tmdb" }, ids.Select(i => i.Attribute("type")!.Value));
        Assert.Equal(new[] { "tt1361336", "587807" }, ids.Select(i => i.Value));
        Assert.Equal("true", ids[0].Attribute("default")!.Value);
        Assert.Null(ids[1].Attribute("default"));
    }

    /// <summary>
    /// An episode names its show, season, number, and air date, and a film its premiere.
    /// True: Jellyfin files the episode under the right show and season without guessing from the name.
    /// False: a renamed episode would land in the wrong season.
    /// </summary>
    [Fact]
    public void Build_Episode_UsesAiredAndNumbers()
    {
        var item = new BaseItemDto
        {
            Type = BaseItemDto_Type.Episode,
            SeriesName = "Show",
            ParentIndexNumber = 2,
            IndexNumber = 14,
            PremiereDate = new DateTimeOffset(2024, 3, 1, 23, 30, 0, TimeSpan.FromHours(-5))
        };

        var root = XDocument.Parse(LinkNfo.Build(item)).Root!;

        Assert.Equal("Show", root.Element("showtitle")!.Value);
        Assert.Equal("2", root.Element("season")!.Value);
        Assert.Equal("14", root.Element("episode")!.Value);
        Assert.Equal("2024-03-02", root.Element("aired")!.Value);
        Assert.Null(root.Element("premiered"));
    }

    /// <summary>
    /// Writing puts the .nfo beside the link with no temporary file left.
    /// True: the library holds one finished .nfo per link.
    /// False: a stray temp file would sit in the user's library.
    /// </summary>
    [Fact]
    public void Write_NoFileThere_WritesBesideTheLink()
    {
        var link = Path.Combine(_dir, "Film.strm");

        var written = LinkNfo.Write(link, new BaseItemDto { Type = BaseItemDto_Type.Movie, Name = "Film" });

        var path = Path.Combine(_dir, "Film.nfo");
        Assert.True(written);
        Assert.True(LinkNfo.IsOurs(path));
        Assert.False(File.Exists(path + ".serversync-tmp"));
    }
}
