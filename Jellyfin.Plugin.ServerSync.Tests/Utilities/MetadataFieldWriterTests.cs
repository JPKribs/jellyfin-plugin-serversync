using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

/// <summary>
/// A metadata blob changes only the fields it holds, clears the ones it holds as empty, and reports a change only when one was made.
/// </summary>
public sealed class MetadataFieldWriterTests
{
    private static string Blob(Dictionary<string, object?> fields) => JsonSerializer.Serialize(fields);

    private static Movie Film() => new()
    {
        Name = "Film",
        Overview = "About it",
        OfficialRating = "PG",
        ProductionYear = 2001,
        CommunityRating = 7.5f,
        Genres = new[] { "Drama" },
        Tags = new[] { "tag" }
    };

    /// <summary>
    /// No blob, or an empty one, changes nothing.
    /// True: a row with no source metadata leaves the local item as it is.
    /// False: an empty blob would throw or wipe the item.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Apply_NoBlob_ChangesNothing(string? json)
    {
        var movie = Film();

        var changed = MetadataFieldWriter.Apply(movie, json, syncGenres: true, syncTags: true);

        Assert.False(changed);
        Assert.Equal("Film", movie.Name);
    }

    /// <summary>
    /// A blob that is the JSON null is refused, and one that is not JSON fails to parse.
    /// True: a damaged row errors and is retried, and the item is not touched.
    /// False: a damaged blob would be taken as "no fields" and the row marked applied.
    /// </summary>
    [Fact]
    public void Apply_BrokenBlob_Throws()
    {
        var movie = Film();

        Assert.Throws<InvalidOperationException>(() => MetadataFieldWriter.Apply(movie, "null", syncGenres: true, syncTags: true));
        Assert.Throws<JsonException>(() => MetadataFieldWriter.Apply(movie, "{not json", syncGenres: true, syncTags: true));
        Assert.Equal("About it", movie.Overview);
    }

    /// <summary>
    /// A missing item is refused.
    /// True: the caller fails at once with the argument named.
    /// False: the apply would fail with a null reference and no cause.
    /// </summary>
    [Fact]
    public void Apply_NullItem_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => MetadataFieldWriter.Apply(null!, "{}", syncGenres: true, syncTags: true));
    }

    /// <summary>
    /// A blob with no fields, or with the values the item already has, reports no change.
    /// True: an item that already matches is not saved again.
    /// False: every run would rewrite every item and raise a change event each time.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Apply_NothingDiffers_ReportsNoChange(bool emptyBlob)
    {
        var movie = Film();
        var json = emptyBlob
            ? "{}"
            : Blob(new() { ["Name"] = "Film", ["Overview"] = "About it", ["ProductionYear"] = 2001, ["CommunityRating"] = 7.5, ["Genres"] = new[] { "Drama" }, ["Tags"] = new[] { "tag" } });

        var changed = MetadataFieldWriter.Apply(movie, json, syncGenres: true, syncTags: true);

        Assert.False(changed);
    }

    /// <summary>
    /// A field the blob does not hold is left alone while a field it holds is written.
    /// True: a partial blob updates only what it names.
    /// False: a blob with one field would blank every other field of the item.
    /// </summary>
    [Fact]
    public void Apply_FieldAbsent_IsLeftAlone()
    {
        var movie = Film();

        var changed = MetadataFieldWriter.Apply(movie, Blob(new() { ["Tagline"] = "A line" }), syncGenres: true, syncTags: true);

        Assert.True(changed);
        Assert.Equal("A line", movie.Tagline);
        Assert.Equal("About it", movie.Overview);
        Assert.Equal(2001, movie.ProductionYear);
        Assert.Equal(new[] { "Drama" }, movie.Genres);
    }

    /// <summary>
    /// A field the blob holds as null is cleared, for text and for numbers.
    /// True: a value removed on the source is removed here.
    /// False: a cleared overview or rating on the source would stay here forever.
    /// </summary>
    [Fact]
    public void Apply_FieldNull_IsCleared()
    {
        var movie = Film();
        var json = Blob(new() { ["Overview"] = null, ["OfficialRating"] = null, ["ProductionYear"] = null, ["CommunityRating"] = null });

        var changed = MetadataFieldWriter.Apply(movie, json, syncGenres: true, syncTags: true);

        Assert.True(changed);
        Assert.Null(movie.Overview);
        Assert.Null(movie.OfficialRating);
        Assert.Null(movie.ProductionYear);
        Assert.Null(movie.CommunityRating);
    }

    /// <summary>
    /// A null or empty name is never written.
    /// True: an item always keeps a name.
    /// False: a source blob with a blank name would leave a nameless item in the library.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Apply_NameNullOrEmpty_KeepsTheName(string? name)
    {
        var movie = Film();

        var changed = MetadataFieldWriter.Apply(movie, Blob(new() { ["Name"] = name }), syncGenres: true, syncTags: true);

        Assert.False(changed);
        Assert.Equal("Film", movie.Name);
    }

    /// <summary>
    /// Genres and tags are written only when their switch is on.
    /// True: turning genre or tag sync off keeps this server's own lists.
    /// False: the switch would do nothing and local genres or tags would be overwritten.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Apply_GenresAndTags_FollowTheirSwitches(bool syncGenres, bool syncTags)
    {
        var movie = Film();
        var json = Blob(new() { ["Genres"] = new[] { "Comedy" }, ["Tags"] = new[] { "new" } });

        var changed = MetadataFieldWriter.Apply(movie, json, syncGenres, syncTags);

        Assert.Equal(syncGenres || syncTags, changed);
        Assert.Equal(syncGenres ? new[] { "Comedy" } : new[] { "Drama" }, movie.Genres);
        Assert.Equal(syncTags ? new[] { "new" } : new[] { "tag" }, movie.Tags);
    }

    /// <summary>
    /// Genres in another order count as a change and are written in the source's order.
    /// True: the order a source shows its genres in carries over.
    /// False: reordered genres on the source would never reach this server.
    /// </summary>
    [Fact]
    public void Apply_GenresReordered_WritesTheSourceOrder()
    {
        var movie = Film();
        movie.Genres = new[] { "Drama", "Comedy" };

        var changed = MetadataFieldWriter.Apply(movie, Blob(new() { ["Genres"] = new[] { "Comedy", "Drama" } }), syncGenres: true, syncTags: true);

        Assert.True(changed);
        Assert.Equal(new[] { "Comedy", "Drama" }, movie.Genres);
    }

    /// <summary>
    /// A date on the same UTC day is not a change, another day is, and a null date clears it.
    /// True: a premiere date that differs only by time of day does not requeue the item.
    /// False: every scan would rewrite dates that Jellyfin stored with a different time.
    /// </summary>
    [Theory]
    [InlineData("2020-05-01T23:00:00Z", false, 1)]
    [InlineData("2020-05-02T00:00:00Z", true, 2)]
    [InlineData(null, true, 0)]
    public void Apply_PremiereDate_ComparesByDay(string? incoming, bool expectedChange, int expectedDay)
    {
        var movie = Film();
        movie.PremiereDate = new DateTime(2020, 5, 1, 4, 0, 0, DateTimeKind.Utc);

        var changed = MetadataFieldWriter.Apply(movie, Blob(new() { ["PremiereDate"] = incoming }), syncGenres: true, syncTags: true);

        Assert.Equal(expectedChange, changed);
        Assert.Equal(expectedDay, movie.PremiereDate?.Day ?? 0);
    }

    /// <summary>
    /// Provider ids become the source's set, while this plugin's own keys on the item stay.
    /// True: an id removed on the source is removed here, and the Identify pin survives.
    /// False: a stale id would keep matching the wrong title, or the plugin's own mark would be lost.
    /// </summary>
    [Fact]
    public void Apply_ProviderIds_MatchTheSourceAndKeepOwnKeys()
    {
        var movie = Film();
        movie.ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Tmdb"] = "1", ["Stale"] = "x", ["ServerSyncPin"] = "keep" };
        var json = Blob(new() { ["ProviderIds"] = new Dictionary<string, string> { ["Tmdb"] = "42", ["Imdb"] = "tt1", ["Empty"] = string.Empty, ["ServerSyncOther"] = "theirs" } });

        var changed = MetadataFieldWriter.Apply(movie, json, syncGenres: true, syncTags: true);

        Assert.True(changed);
        Assert.Equal(new[] { "Imdb", "ServerSyncPin", "Tmdb" }, movie.ProviderIds.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(("42", "tt1", "keep"), (movie.ProviderIds["Tmdb"], movie.ProviderIds["Imdb"], movie.ProviderIds["ServerSyncPin"]));
    }

    /// <summary>
    /// A video takes the aspect ratio and 3D format, and an unknown or null 3D format clears it.
    /// True: video only fields reach a video and a format the source dropped is dropped here.
    /// False: a 3D flag would stick to a title after the source removed it.
    /// </summary>
    [Theory]
    [InlineData("HalfSideBySide", Video3DFormat.HalfSideBySide)]
    [InlineData("NotAFormat", null)]
    [InlineData(null, null)]
    public void Apply_VideoFields_AreWrittenToAVideo(string? format, Video3DFormat? expected)
    {
        var movie = Film();
        movie.Video3DFormat = Video3DFormat.MVC;

        var changed = MetadataFieldWriter.Apply(movie, Blob(new() { ["AspectRatio"] = "16:9", ["Video3DFormat"] = format }), syncGenres: true, syncTags: true);

        Assert.True(changed);
        Assert.Equal("16:9", movie.AspectRatio);
        Assert.Equal(expected, movie.Video3DFormat);
    }

    /// <summary>
    /// An item that is not a video ignores the video only fields.
    /// True: a folder's blob with video fields applies without error and without a change.
    /// False: a folder would be saved on every run for fields it cannot hold.
    /// </summary>
    [Fact]
    public void Apply_VideoFieldsOnAFolder_AreIgnored()
    {
        var folder = new Folder { Name = "Folder" };

        var changed = MetadataFieldWriter.Apply(folder, Blob(new() { ["AspectRatio"] = "16:9", ["Video3DFormat"] = "MVC" }), syncGenres: true, syncTags: true);

        Assert.False(changed);
    }

    /// <summary>
    /// With lock sync on, the locked fields and the lock flag are written, entries that are not fields are dropped, and a null flag unlocks.
    /// True: an item locked on the source is locked here, and one unlocked there is unlocked here.
    /// False: a provider refresh here would overwrite fields the source's operator locked.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    public void Apply_LocksWithSyncOn_AreWritten(bool? lockData, bool expectedLocked)
    {
        var movie = Film();
        movie.IsLocked = !expectedLocked;
        var json = Blob(new() { ["LockedFields"] = new object[] { "Genres", "NotAField", 5, "Overview" }, ["LockData"] = lockData });

        var changed = MetadataFieldWriter.Apply(movie, json, syncGenres: true, syncTags: true, syncLocks: true);

        Assert.True(changed);
        Assert.Equal(new[] { MetadataField.Genres, MetadataField.Overview }, movie.LockedFields);
        Assert.Equal(expectedLocked, movie.IsLocked);
    }

    /// <summary>
    /// With lock sync off, the locked fields and the lock flag are left alone.
    /// True: this server keeps its own locks when the operator turned lock sync off.
    /// False: the switch would do nothing and local locks would be replaced.
    /// </summary>
    [Fact]
    public void Apply_LocksWithSyncOff_AreLeftAlone()
    {
        var movie = Film();
        movie.LockedFields = new[] { MetadataField.Name };
        var json = Blob(new() { ["LockedFields"] = new[] { "Genres" }, ["LockData"] = true });

        var changed = MetadataFieldWriter.Apply(movie, json, syncGenres: true, syncTags: true, syncLocks: false);

        Assert.False(changed);
        Assert.Equal(new[] { MetadataField.Name }, movie.LockedFields);
        Assert.False(movie.IsLocked);
    }

    /// <summary>
    /// No people blob, an empty one, and an empty list all give no people.
    /// True: an item with no cast on the source gets an empty cast.
    /// False: a missing blob would throw and fail the item's whole apply.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    public void ReadPeople_NothingListed_ReturnsNone(string? json)
    {
        var people = MetadataFieldWriter.ReadPeople(json);

        Assert.Empty(people);
    }

    /// <summary>
    /// A person's kind is read when it is a known kind, and left at the default when it is unknown or missing.
    /// True: a director stays a director and an odd kind does not drop the person.
    /// False: a person with a kind this build does not know would vanish from the cast.
    /// </summary>
    [Fact]
    public void ReadPeople_KnownAndUnknownKinds_KeepEveryNamedPerson()
    {
        var people = MetadataFieldWriter.ReadPeople("[{\"Name\":\"A\",\"Type\":\"Director\"},{\"Name\":\"B\",\"Type\":\"NotAKind\"},{\"Name\":\"C\"}]");

        Assert.Equal(new[] { "A", "B", "C" }, people.Select(p => p.Name).ToArray());
        Assert.Equal(PersonKind.Director, people[0].Type);
        Assert.Equal(new PersonInfo().Type, people[1].Type);
        Assert.Equal(new PersonInfo().Type, people[2].Type);
    }

    /// <summary>
    /// People with an empty name are skipped and the rest are numbered without a gap.
    /// True: billing order stays dense after a nameless entry is dropped.
    /// False: a gap in the order would make the cast compare as changed on every scan.
    /// </summary>
    [Fact]
    public void ReadPeople_EmptyNameInTheMiddle_IsSkippedWithoutAGap()
    {
        var people = MetadataFieldWriter.ReadPeople("[{\"Name\":\"A\"},{\"Name\":\"\"},{\"Name\":\"C\"}]");

        Assert.Equal(new[] { "A", "C" }, people.Select(p => p.Name).ToArray());
        Assert.Equal(new int?[] { 0, 1 }, people.Select(p => p.SortOrder).ToArray());
    }

    /// <summary>
    /// An empty blob, an empty list, and the JSON null give no studios, and a list keeps its order.
    /// True: an item with no studios on the source ends with none here.
    /// False: a missing blob would throw and fail the item's whole apply.
    /// </summary>
    [Theory]
    [InlineData("", 0)]
    [InlineData("[]", 0)]
    [InlineData("null", 0)]
    [InlineData("[\"B\",\"A\"]", 2)]
    public void ReadStudios_EachShape_ReturnsTheNamesInOrder(string json, int expectedCount)
    {
        var studios = MetadataFieldWriter.ReadStudios(json);

        Assert.Equal(expectedCount, studios.Length);
        Assert.Equal(expectedCount == 0 ? Array.Empty<string>() : new[] { "B", "A" }, studios);
    }
}
