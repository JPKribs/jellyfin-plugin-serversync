using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// Always keep must copy only what is picked on a Link library mapping and leave every other file as its mapping says.
/// </summary>
public sealed class AlwaysKeepTests
{
    private const string Show = "/src/shows/Kept Show";
    private const string Episode = "/src/shows/Kept Show/Season 01/E01.mkv";

    /// <summary>
    /// A missing mapping is a caller's mistake and is refused at once.
    /// True: the mistake is named where it was made.
    /// False: a null mapping would fail later with no hint of which argument was wrong.
    /// </summary>
    [Fact]
    public void Covers_NullMapping_Throws()
    {
        LibraryMapping mapping = null!;

        Action act = () => AlwaysKeep.Covers(mapping, Episode);

        Assert.Throws<ArgumentNullException>(act);
    }

    /// <summary>
    /// Picks left over on a mapping that copies, or is being promoted, cover nothing.
    /// True: Always keep only ever applies to a Link mapping.
    /// False: stale picks would change how a Copy mapping's files are judged.
    /// </summary>
    /// <param name="mode">The mapping's storage.</param>
    [Theory]
    [InlineData(StorageMode.Copy)]
    [InlineData(StorageMode.Promoting)]
    public void Covers_MappingThatIsNotLink_IsFalse(StorageMode mode)
    {
        var mapping = Mapping(mode, Show);

        var covered = AlwaysKeep.Covers(mapping, Episode);

        Assert.False(covered);
    }

    /// <summary>
    /// A Link mapping with no picks, as a null list or an empty one, covers nothing.
    /// True: a mapping nobody picked from links every file.
    /// False: a mapping without picks would throw or copy files nobody asked to keep.
    /// </summary>
    /// <param name="hasList">Whether the mapping holds an empty list or none.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Covers_NoPicks_IsFalse(bool hasList)
    {
        var mapping = new LibraryMapping { StorageMode = StorageMode.Link, SourceRootPath = "/src/shows", PermanentItems = hasList ? new List<FilteredItem>() : null };

        var covered = AlwaysKeep.Covers(mapping, Episode);

        Assert.False(covered);
    }

    /// <summary>
    /// A file with no path is never covered, whatever is picked.
    /// True: an item the source sent without a path stays a link candidate.
    /// False: a pathless item would be taken for a picked one, or throw.
    /// </summary>
    /// <param name="path">The file's path on the source.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Covers_NoSourcePath_IsFalse(string? path)
    {
        var mapping = Mapping(StorageMode.Link, Show);

        var covered = AlwaysKeep.Covers(mapping, path);

        Assert.False(covered);
    }

    /// <summary>
    /// A file under a picked show is stored as a copy, and a file outside every pick follows its mapping.
    /// True: only what was picked is downloaded in full.
    /// False: a picked show would stay links, or the whole mapping would be copied.
    /// </summary>
    /// <param name="path">The file's path on the source.</param>
    /// <param name="expected">How the file is stored.</param>
    [Theory]
    [InlineData(Episode, StorageMode.Copy)]
    [InlineData("/src/shows/Other Show/Season 01/E01.mkv", StorageMode.Link)]
    [InlineData("/src/shows/Kept Show 2/Season 01/E01.mkv", StorageMode.Link)]
    public void StorageFor_FollowsThePicks(string path, StorageMode expected)
    {
        var mapping = Mapping(StorageMode.Link, Show);

        var storage = AlwaysKeep.StorageFor(mapping, path);

        Assert.Equal(expected, storage);
    }

    /// <summary>
    /// A Copy mapping stores every file as a copy, picks or not.
    /// True: the mapping's own storage is the answer when Always keep does not apply.
    /// False: a Copy mapping could come back as something other than Copy.
    /// </summary>
    [Fact]
    public void StorageFor_CopyMapping_IsCopy()
    {
        var mapping = Mapping(StorageMode.Copy, Show);

        var storage = AlwaysKeep.StorageFor(mapping, "/src/shows/Other Show/E01.mkv");

        Assert.Equal(StorageMode.Copy, storage);
    }

    /// <summary>
    /// A missing path on either side, or a pick that sits at the root with no folder, is never a version of a film.
    /// True: only two files in one film folder can be versions of each other.
    /// False: a null path would throw, or loose files at a root would be kept as versions.
    /// </summary>
    /// <param name="picked">The picked item's path.</param>
    /// <param name="other">The other file's path.</param>
    [Theory]
    [InlineData(null, "/src/m/Film/Film.mkv")]
    [InlineData("", "/src/m/Film/Film.mkv")]
    [InlineData("/src/m/Film/Film.mkv", null)]
    [InlineData("/src/m/Film/Film.mkv", "")]
    [InlineData("/Film.mkv", "/Film - 4K.mkv")]
    [InlineData("/src/m/Film/Film.mkv", "/src/m/Film/Extras/Film - Trailer.mkv")]
    [InlineData("/src/m/Film/Film.mkv", "/src/m/Film/Other.mkv")]
    public void SameFilm_NotTwoVersionsInOneFolder_IsFalse(string? picked, string? other)
    {
        var same = AlwaysKeep.SameFilm(picked, other);

        Assert.False(same);
    }

    private static LibraryMapping Mapping(StorageMode mode, string pick) => new()
    {
        StorageMode = mode,
        SourceRootPath = "/src/shows",
        PermanentItems = new List<FilteredItem> { new() { ItemId = "pick", Path = pick } }
    };
}
