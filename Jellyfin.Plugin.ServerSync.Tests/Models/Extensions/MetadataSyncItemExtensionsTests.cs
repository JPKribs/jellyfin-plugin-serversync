using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Extensions;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Models.Extensions;

/// <summary>
/// A metadata row reaches the dashboard with its library names resolved, its blobs only when asked for, and one change flag that agrees with its categories.
/// </summary>
public sealed class MetadataSyncItemExtensionsTests
{
    private static readonly DateTime At = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private static MetadataSyncItem Row()
    {
        var row = new MetadataSyncItem
        {
            Id = 9,
            SourceLibraryId = "src-lib",
            LocalLibraryId = "loc-lib",
            SourceItemId = "src-item",
            LocalItemId = "loc-item",
            ItemName = "Film",
            SourcePath = "/media/movies/film.mkv",
            LocalPath = "/local/movies/film.mkv",
            ServerKey = "server-key",
            Status = SyncStatus.Ignored,
            StatusDate = At,
            LastSyncTime = At.AddHours(1),
            Reason = "ignored by hand"
        };
        row.Metadata.UpdateSource("{\"Name\":\"A\"}");
        row.Metadata.Local = "{\"Name\":\"A\"}";
        row.People.UpdateSource("[{\"Name\":\"Jane\",\"Type\":\"Actor\"}]");
        row.People.Local = "[{\"Name\":\"Jane\",\"Type\":\"Actor\"}]";
        row.Studios.UpdateSource("[\"S1\"]");
        row.Studios.Local = "[\"S1\"]";
        return row;
    }

    private static List<LibraryMapping> Mappings() => new()
    {
        new LibraryMapping { SourceLibraryId = "other", SourceLibraryName = "Other", LocalLibraryName = "Other Here" },
        new LibraryMapping { SourceLibraryId = "src-lib", SourceLibraryName = "Movies There", LocalLibraryName = "Movies Here" }
    };

    /// <summary>
    /// The library names come from the mapping whose source library matches the row.
    /// True: the row shows the names of its own libraries.
    /// False: every row would show the first mapping's names.
    /// </summary>
    [Fact]
    public void ToDto_MatchingMapping_ResolvesLibraryNames()
    {
        var row = Row();

        var dto = row.ToDto(Mappings(), "https://source.example");

        Assert.Equal("Movies There", dto.SourceLibraryName);
        Assert.Equal("Movies Here", dto.LocalLibraryName);
    }

    /// <summary>
    /// No mappings, an empty list, or no match leaves the library names empty and still maps the row.
    /// True: a row whose mapping was removed still lists.
    /// False: removing a mapping would break the Sync tab for its leftover rows.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ToDto_NoMatchingMapping_LeavesLibraryNamesEmpty(int scenario)
    {
        var row = Row();
        var mappings = scenario switch
        {
            0 => null,
            1 => new List<LibraryMapping>(),
            _ => new List<LibraryMapping> { new() { SourceLibraryId = "other", SourceLibraryName = "Other" } }
        };

        var dto = row.ToDto(mappings, "https://source.example");

        Assert.Null(dto.SourceLibraryName);
        Assert.Null(dto.LocalLibraryName);
        Assert.Equal("src-item", dto.SourceItemId);
    }

    /// <summary>
    /// With blobs asked for, every category's source and local value is carried.
    /// True: the detail view can show both sides of each category.
    /// False: the detail view would open empty.
    /// </summary>
    [Fact]
    public void ToDto_IncludeBlobs_CarriesEveryCategoryValue()
    {
        var row = Row();
        row.Images.UpdateSource("{\"Primary\":[{\"ImageType\":\"Primary\",\"Size\":10}]}");
        row.Images.Local = "{\"Primary\":[{\"ImageType\":\"Primary\",\"Size\":10}]}";

        var dto = row.ToDto(null, null, includeBlobs: true);

        Assert.Equal((row.Metadata.Source, row.Metadata.Local), (dto.SourceMetadataValue, dto.LocalMetadataValue));
        Assert.Equal((row.Images.Source, row.Images.Local), (dto.SourceImagesValue, dto.LocalImagesValue));
        Assert.Equal((row.People.Source, row.People.Local), (dto.SourcePeopleValue, dto.LocalPeopleValue));
        Assert.Equal((row.Studios.Source, row.Studios.Local), (dto.SourceStudiosValue, dto.LocalStudiosValue));
    }

    /// <summary>
    /// Without blobs, every category value is left out while the identity stays.
    /// True: a list page stays small.
    /// False: every page of the Sync tab would carry each row's full metadata and images.
    /// </summary>
    [Fact]
    public void ToDto_WithoutBlobs_LeavesEveryCategoryValueOut()
    {
        var row = Row();

        var dto = row.ToDto(null, null, includeBlobs: false);

        Assert.Null(dto.SourceMetadataValue);
        Assert.Null(dto.LocalMetadataValue);
        Assert.Null(dto.SourceImagesValue);
        Assert.Null(dto.LocalImagesValue);
        Assert.Null(dto.SourcePeopleValue);
        Assert.Null(dto.LocalPeopleValue);
        Assert.Null(dto.SourceStudiosValue);
        Assert.Null(dto.LocalStudiosValue);
        Assert.Equal("Film", dto.ItemName);
    }

    /// <summary>
    /// A row whose categories all match reports no change and no detail.
    /// True: a settled row shows no badge and no difference text.
    /// False: a settled row would show an empty difference as if something were wrong.
    /// </summary>
    [Fact]
    public void ToDto_NothingDiffers_ReportsNoChangesAndNoDetail()
    {
        var row = Row();

        var dto = row.ToDto(null, null);

        Assert.False(dto.HasChanges);
        Assert.Null(dto.MetadataChangesDetail);
        Assert.Null(dto.ImagesChangesDetail);
        Assert.Equal("No changes", dto.ChangesSummary);
    }

    /// <summary>
    /// A difference in only people, or only studios, sets that flag and the overall flag.
    /// True: a row with one changed category shows as changed, under the right category.
    /// False: a people or studio change would be hidden behind matching metadata.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToDto_OneCategoryDiffers_SetsThatFlagAndTheOverallFlag(bool people)
    {
        var row = Row();
        if (people)
        {
            row.People.Local = "[{\"Name\":\"John\",\"Type\":\"Actor\"}]";
        }
        else
        {
            row.Studios.Local = "[\"S2\"]";
        }

        var dto = row.ToDto(null, null);

        Assert.True(dto.HasChanges);
        Assert.Equal(people, dto.HasPeopleChanges);
        Assert.Equal(!people, dto.HasStudiosChanges);
        Assert.False(dto.HasMetadataChanges);
        Assert.False(dto.HasImagesChanges);
    }

    /// <summary>
    /// The status goes out as its name, the reason as the error, and the server as given.
    /// True: the dashboard can label the row, show why, and link to its source.
    /// False: the status would be a bare number and the reason would be lost.
    /// </summary>
    [Fact]
    public void ToDto_IgnoredRow_CarriesStatusTextReasonAndServer()
    {
        var row = Row();

        var dto = row.ToDto(null, "https://source.example");

        Assert.Equal("Ignored", dto.Status);
        Assert.Equal("ignored by hand", dto.ErrorMessage);
        Assert.Equal("https://source.example", dto.SourceServerUrl);
        Assert.Equal("server-key", dto.ServerKey);
        Assert.Equal(At, dto.StatusDate);
        Assert.Equal(At.AddHours(1), dto.LastSyncTime);
    }

    /// <summary>
    /// A missing row is refused.
    /// True: the caller fails at once with the argument named.
    /// False: the page would fail with a null reference and no cause.
    /// </summary>
    [Fact]
    public void ToDto_NullRow_Throws()
    {
        MetadataSyncItem? row = null;

        Assert.Throws<ArgumentNullException>(() => row!.ToDto(null, null));
    }
}
