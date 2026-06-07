using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.PeopleSync;

public class PeopleSyncItemTests
{
    private static PeopleSyncItem MakeItem(string? localPersonId = "loc-person-1") => new()
    {
        PersonName = "Test Person",
        SourcePersonId = System.Guid.NewGuid().ToString("N"),
        LocalPersonId = localPersonId
    };

    /// <summary>
    /// HasMetadataChanges is false when LocalPersonId is missing.
    /// True: rows without a local person match aren't queued for apply.
    /// False: apply would try to write metadata to a non-existent local person and crash.
    /// </summary>
    [Fact]
    public void HasMetadataChanges_NoLocalPersonId_IsFalse()
    {
        var item = MakeItem(localPersonId: null);
        item.Metadata.UpdateSource("{\"a\":1}");

        Assert.False(item.HasMetadataChanges);
    }

    /// <summary>
    /// HasMetadataChanges is true when source and local differ.
    /// True: real metadata diffs queue for sync.
    /// False: person metadata changes would silently never propagate.
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
    /// HasImagesChanges is false when LocalPersonId is missing.
    /// True: image apply requires a local person; missing disables it.
    /// False: apply would crash trying to write a profile image to a missing local person.
    /// </summary>
    [Fact]
    public void HasImagesChanges_NoLocalPersonId_IsFalse()
    {
        var item = MakeItem(localPersonId: null);
        item.Images.UpdateSource("{\"Primary\":[{\"Size\":100,\"Tag\":\"t\"}]}");

        Assert.False(item.HasImagesChanges);
    }

    /// <summary>
    /// HasImagesChanges is true when source and local manifests differ.
    /// True: real image diffs queue for sync.
    /// False: person image changes would silently never propagate.
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
    /// HasChanges aggregates over both categories.
    /// True: a diff in either Metadata or Images sets the aggregate flag.
    /// False: rows with a single-category diff wouldn't queue if HasChanges only checked one.
    /// </summary>
    [Fact]
    public void HasChanges_AggregatesAcrossCategories()
    {
        var item = MakeItem();
        item.Metadata.UpdateSource("{\"a\":1}");
        item.Metadata.Local = "{\"a\":2}";

        Assert.True(item.HasChanges);
    }

    /// <summary>
    /// MarkSynced advances hashes on both categories.
    /// True: each category's fast-path activates on subsequent refreshes.
    /// False: only one category would short-circuit, the other re-evaluates every refresh.
    /// </summary>
    [Fact]
    public void MarkSynced_AdvancesBothCategoryHashes()
    {
        var item = MakeItem();
        item.Metadata.UpdateSource("{\"a\":1}");
        item.Images.UpdateSource("{\"Primary\":[{\"Size\":100,\"Tag\":\"t\"}]}");

        item.MarkSynced();

        Assert.Equal(item.Metadata.SourceHash, item.Metadata.SyncedHash);
        Assert.Equal(item.Images.SourceHash, item.Images.SyncedHash);
    }
}
