using System;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Extensions;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Models.Extensions;

/// <summary>
/// A person row reaches the dashboard with both sides of its values and a difference text only where the sides differ.
/// </summary>
public sealed class PeopleSyncItemExtensionsTests
{
    private static readonly DateTime At = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private static PeopleSyncItem Row()
    {
        var row = new PeopleSyncItem
        {
            Id = 4,
            PersonName = "Jane Doe",
            SourcePersonId = "src-person",
            LocalPersonId = "loc-person",
            ServerKey = "server-key",
            Status = SyncStatus.Queued,
            StatusDate = At,
            LastSyncTime = At.AddHours(1),
            Reason = "waiting"
        };
        row.Metadata.UpdateSource("{\"Overview\":\"Bio\"}");
        row.Metadata.Local = "{\"Overview\":\"Bio\"}";
        return row;
    }

    /// <summary>
    /// The person, both ids, both sides of each value, and the status fields copy across.
    /// True: the dashboard shows the person the row is about and where it stands.
    /// False: a row would show another field's value or lose its status.
    /// </summary>
    [Fact]
    public void ToDto_FullRow_CopiesIdentityValuesAndStatus()
    {
        var row = Row();
        row.Images.UpdateSource("{\"Primary\":[{\"ImageType\":\"Primary\",\"Size\":10}]}");
        row.Images.Local = "{\"Primary\":[{\"ImageType\":\"Primary\",\"Size\":10}]}";

        var dto = row.ToDto("https://source.example");

        Assert.Equal((4L, "Jane Doe", "src-person", "loc-person"), (dto.Id, dto.PersonName, dto.SourcePersonId, dto.LocalPersonId));
        Assert.Equal((row.Metadata.Source, row.Metadata.Local), (dto.SourceMetadataValue, dto.LocalMetadataValue));
        Assert.Equal((row.Images.Source, row.Images.Local), (dto.SourceImagesValue, dto.LocalImagesValue));
        Assert.Equal(("Queued", "waiting", "https://source.example", "server-key"), (dto.Status, dto.ErrorMessage, dto.SourceServerUrl, dto.ServerKey));
        Assert.Equal((At, At.AddHours(1)), (dto.StatusDate, dto.LastSyncTime));
    }

    /// <summary>
    /// Matching sides report no change and no difference text.
    /// True: a settled person shows no badge.
    /// False: a settled person would show an empty difference as if something were wrong.
    /// </summary>
    [Fact]
    public void ToDto_NothingDiffers_ReportsNoChangesAndNoDetail()
    {
        var row = Row();

        var dto = row.ToDto(null);

        Assert.False(dto.HasChanges);
        Assert.False(dto.HasMetadataChanges);
        Assert.False(dto.HasImagesChanges);
        Assert.Null(dto.MetadataChangesDetail);
        Assert.Null(dto.ImagesChangesDetail);
    }

    /// <summary>
    /// Metadata that differs is flagged and the field that differs is named.
    /// True: the dashboard says which field of the person changed.
    /// False: a changed biography would show a badge with no way to tell what changed.
    /// </summary>
    [Fact]
    public void ToDto_MetadataDiffers_NamesTheField()
    {
        var row = Row();
        row.Metadata.Local = "{\"Overview\":\"Old bio\"}";

        var dto = row.ToDto(null);

        Assert.True(dto.HasChanges);
        Assert.True(dto.HasMetadataChanges);
        Assert.Equal("Overview", dto.MetadataChangesDetail);
        Assert.Null(dto.ImagesChangesDetail);
    }

    /// <summary>
    /// Images that differ are flagged and the image type that differs is named.
    /// True: the dashboard says which image of the person changed.
    /// False: a changed photo would show a badge with no way to tell what changed.
    /// </summary>
    [Fact]
    public void ToDto_ImagesDiffer_NamesTheImageType()
    {
        var row = Row();
        row.Images.UpdateSource("{\"Primary\":[{\"ImageType\":\"Primary\",\"ImageIndex\":0,\"Size\":500}]}");
        row.Images.Local = "{\"Primary\":[{\"ImageType\":\"Primary\",\"ImageIndex\":0,\"Size\":400}]}";

        var dto = row.ToDto(null);

        Assert.True(dto.HasImagesChanges);
        Assert.Contains("Primary", dto.ImagesChangesDetail);
        Assert.Null(dto.MetadataChangesDetail);
    }

    /// <summary>
    /// A person with no local match reports no change even when the sides differ.
    /// True: a person this server does not hold is not shown as work to do.
    /// False: the row would show a change the apply step can never make.
    /// </summary>
    [Fact]
    public void ToDto_NoLocalPerson_ReportsNoChanges()
    {
        var row = Row();
        row.LocalPersonId = null;
        row.Metadata.Local = "{\"Overview\":\"Old bio\"}";

        var dto = row.ToDto(null);

        Assert.False(dto.HasChanges);
        Assert.Null(dto.MetadataChangesDetail);
    }

    /// <summary>
    /// A missing row is refused.
    /// True: the caller fails at once with the argument named.
    /// False: the page would fail with a null reference and no cause.
    /// </summary>
    [Fact]
    public void ToDto_NullRow_Throws()
    {
        PeopleSyncItem? row = null;

        Assert.Throws<ArgumentNullException>(() => row!.ToDto(null));
    }
}
