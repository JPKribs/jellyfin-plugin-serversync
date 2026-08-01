using Jellyfin.Plugin.ServerSync.Models;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.MetadataSync;

public class MetadataSyncItemTests
{
    private static MetadataSyncItem MakeItem(string? localItemId = "loc-1") => new()
    {
        SourceLibraryId = "src-lib",
        LocalLibraryId = "loc-lib",
        SourceItemId = "src-item",
        LocalItemId = localItemId
    };

    /// <summary>
    /// HasMetadataChanges is false when LocalItemId is missing.
    /// True: rows with no local match correctly report no work for the apply phase.
    /// False: apply would try to write metadata to a non-existent local item and crash.
    /// </summary>
    [Fact]
    public void HasMetadataChanges_NoLocalItemId_IsFalse()
    {
        var item = MakeItem(localItemId: null);
        item.Metadata.UpdateSource("{\"a\":1}");

        Assert.False(item.HasMetadataChanges);
    }

    /// <summary>
    /// HasMetadataChanges is false when Metadata.Source is empty.
    /// True: rows without source data aren't queued for a no-op apply.
    /// False: apply would dispatch on rows with nothing to write and fail verify.
    /// </summary>
    [Fact]
    public void HasMetadataChanges_NoSource_IsFalse()
    {
        var item = MakeItem();

        Assert.False(item.HasMetadataChanges);
    }

    /// <summary>
    /// HasMetadataChanges is true when source and local are present and differ.
    /// True: real metadata diffs are queued for sync.
    /// False: metadata changes would silently never propagate.
    /// </summary>
    [Fact]
    public void HasMetadataChanges_Diff_IsTrue()
    {
        var item = MakeItem();
        item.Metadata.UpdateSource("{\"a\":1}");
        item.Metadata.Local = "{\"a\":2}";

        Assert.True(item.HasMetadataChanges);
    }

    /// <summary>
    /// HasImagesChanges is false when LocalItemId is missing.
    /// True: rows with no local match aren't queued for image apply.
    /// False: image apply would crash trying to save to a non-existent item.
    /// </summary>
    [Fact]
    public void HasImagesChanges_NoLocalItemId_IsFalse()
    {
        var item = MakeItem(localItemId: null);
        item.Images.UpdateSource("{\"Primary\":[{\"Size\":100,\"Tag\":\"t\"}]}");

        Assert.False(item.HasImagesChanges);
    }

    /// <summary>
    /// HasImagesChanges is true when source and local manifests differ.
    /// True: real image diffs are queued for sync.
    /// False: image changes would silently never propagate.
    /// </summary>
    [Fact]
    public void HasImagesChanges_Diff_IsTrue()
    {
        var item = MakeItem();
        item.Images.UpdateSource("{\"Primary\":[{\"ImageType\":\"Primary\",\"Size\":100,\"Tag\":\"t\"}]}");
        item.Images.Local = "{\"Primary\":[{\"ImageType\":\"Primary\",\"Size\":200,\"Tag\":\"t\"}]}";

        Assert.True(item.HasImagesChanges);
    }

    /// <summary>
    /// HasPeopleChanges is false when LocalItemId is missing.
    /// True: people apply requires a local item; missing LocalItemId correctly disables it.
    /// False: apply would crash trying to write people on a missing local item.
    /// </summary>
    [Fact]
    public void HasPeopleChanges_NoLocalItemId_IsFalse()
    {
        var item = MakeItem(localItemId: null);
        item.People.UpdateSource("[{\"Name\":\"A\"}]");

        Assert.False(item.HasPeopleChanges);
    }

    /// <summary>
    /// HasPeopleChanges is true when source and local people differ.
    /// True: real cast-list diffs are queued for sync.
    /// False: cast changes would silently never propagate.
    /// </summary>
    [Fact]
    public void HasPeopleChanges_Diff_IsTrue()
    {
        var item = MakeItem();
        item.People.UpdateSource("[{\"Name\":\"Alice\"}]");
        item.People.Local = "[{\"Name\":\"Bob\"}]";

        Assert.True(item.HasPeopleChanges);
    }

    /// <summary>
    /// HasStudiosChanges is false when Studios.Source is empty array.
    /// True: an explicit "no studios" source doesn't wipe out local studio assignments.
    /// False: a source that genuinely has no studios would erase local studios.
    /// </summary>
    [Fact]
    public void HasStudiosChanges_EmptySource_IsFalse()
    {
        var item = MakeItem();
        item.Studios.UpdateSource("[]");
        item.Studios.Local = "[\"Studio A\"]";

        Assert.False(item.HasStudiosChanges);
    }

    /// <summary>
    /// HasStudiosChanges is false when LocalItemId is missing.
    /// True: studios apply requires a local item; missing disables it.
    /// False: apply would crash trying to write studios on a missing local item.
    /// </summary>
    [Fact]
    public void HasStudiosChanges_NoLocalItemId_IsFalse()
    {
        var item = MakeItem(localItemId: null);
        item.Studios.UpdateSource("[\"Studio A\"]");

        Assert.False(item.HasStudiosChanges);
    }

    /// <summary>
    /// HasStudiosChanges is true when source has studios that differ from local.
    /// True: real studio diffs are queued for sync.
    /// False: studio changes would silently never propagate.
    /// </summary>
    [Fact]
    public void HasStudiosChanges_Diff_IsTrue()
    {
        var item = MakeItem();
        item.Studios.UpdateSource("[\"Studio A\"]");
        item.Studios.Local = "[\"Studio B\"]";

        Assert.True(item.HasStudiosChanges);
    }

    /// <summary>
    /// HasChanges aggregates over all four categories.
    /// True: any single category with a diff sets the aggregate flag.
    /// False: rows with a diff in one category wouldn't queue if HasChanges only checked some categories.
    /// </summary>
    [Fact]
    public void HasChanges_AggregatesAcrossCategories()
    {
        var item = MakeItem();
        item.People.UpdateSource("[{\"Name\":\"Alice\"}]");
        item.People.Local = "[{\"Name\":\"Bob\"}]";

        Assert.True(item.HasChanges);
    }

    /// <summary>
    /// MarkSynced calls MarkSynced on all four categories.
    /// True: each category's hash advances, so subsequent refreshes can fast-path each.
    /// False: only some categories would short-circuit, the rest would re-evaluate every refresh.
    /// </summary>
    [Fact]
    public void MarkSynced_AdvancesAllCategoryHashes()
    {
        var item = MakeItem();
        item.Metadata.UpdateSource("{\"a\":1}");
        item.Images.UpdateSource("{\"Primary\":[{\"Size\":100,\"Tag\":\"t\"}]}");
        item.People.UpdateSource("[{\"Name\":\"A\"}]");
        item.Studios.UpdateSource("[\"S\"]");

        item.MarkSynced();

        Assert.Equal(item.Metadata.SourceHash, item.Metadata.SyncedHash);
        Assert.Equal(item.Images.SourceHash, item.Images.SyncedHash);
        Assert.Equal(item.People.SourceHash, item.People.SyncedHash);
        Assert.Equal(item.Studios.SourceHash, item.Studios.SyncedHash);
    }

    // ===================================================================
    // Change-detail surfacing. The badges are computed server-side over the
    // full blobs while the modal renders aggregates and fixed field lists,
    // so a badge could say Changes with no visible difference. The DTO now
    // names the reason.
    // ===================================================================

    /// <summary>
    /// A per-index image size difference that display SUMS would hide is
    /// named in the DTO detail.
    /// True: "Images: Changes" is always explainable from the modal.
    /// False: identical-looking rows with a Changes badge, unexplainable.
    /// </summary>
    [Fact]
    public void ToDto_ImagesDiffer_NamesTheDivergingImage()
    {
        var item = new MetadataSyncItem
        {
            SourceLibraryId = "lib",
            SourceItemId = "i1",
            LocalItemId = "local-1",
            StatusDate = System.DateTime.UtcNow
        };
        item.Images.UpdateSource("{\"Backdrop\":[{\"ImageType\":\"Backdrop\",\"ImageIndex\":0,\"Size\":500},{\"ImageType\":\"Backdrop\",\"ImageIndex\":1,\"Size\":400}]}");
        item.Images.Local = "{\"Backdrop\":[{\"ImageType\":\"Backdrop\",\"ImageIndex\":0,\"Size\":400},{\"ImageType\":\"Backdrop\",\"ImageIndex\":1,\"Size\":500}]}";

        var dto = item.ToDto(null, "http://src", includeBlobs: true);

        Assert.True(dto.HasImagesChanges);
        Assert.NotNull(dto.ImagesChangesDetail);
        Assert.Contains("Backdrop", dto.ImagesChangesDetail);
    }

    /// <summary>
    /// A differing metadata field is named even if the modal's fixed field
    /// list were to omit it.
    /// </summary>
    [Fact]
    public void ToDto_MetadataDiffers_NamesTheField()
    {
        var item = new MetadataSyncItem
        {
            SourceLibraryId = "lib",
            SourceItemId = "i1",
            LocalItemId = "local-1",
            StatusDate = System.DateTime.UtcNow
        };
        item.Metadata.UpdateSource("{\"Name\":\"A\",\"Overview\":\"new text\"}");
        item.Metadata.Local = "{\"Name\":\"A\",\"Overview\":\"old text\"}";

        var dto = item.ToDto(null, "http://src", includeBlobs: true);

        Assert.True(dto.HasMetadataChanges);
        Assert.Equal("Overview", dto.MetadataChangesDetail);
    }

    /// <summary>
    /// List views (includeBlobs false) skip the detail computation — it
    /// deserializes both blobs per row and the table only shows badges.
    /// </summary>
    [Fact]
    public void ToDto_ListView_OmitsChangeDetails()
    {
        var item = new MetadataSyncItem
        {
            SourceLibraryId = "lib",
            SourceItemId = "i1",
            LocalItemId = "local-1",
            StatusDate = System.DateTime.UtcNow
        };
        item.Metadata.UpdateSource("{\"Name\":\"A\"}");
        item.Metadata.Local = "{\"Name\":\"B\"}";

        var dto = item.ToDto(null, "http://src", includeBlobs: false);

        Assert.True(dto.HasMetadataChanges);
        Assert.Null(dto.MetadataChangesDetail);
        Assert.Null(dto.ImagesChangesDetail);
    }
}
