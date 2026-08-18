using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Sdk.Generated.Models;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.PeopleSync;

public class PeopleSyncMergeServiceTests
{
    private static BaseItemDto MakeSourcePerson(System.Action<BaseItemDto>? configure = null)
    {
        var dto = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Source Person",
            OriginalTitle = "Original Person",
            Overview = "Career bio"
        };
        configure?.Invoke(dto);
        return dto;
    }

    private static PeopleSyncItem MakeItem(string localPersonId = "loc-person-1") => new()
    {
        PersonName = "Test Person",
        SourcePersonId = Guid.NewGuid().ToString("N"),
        LocalPersonId = localPersonId
    };

    /// <summary>
    /// BuildSourceMetadata returns valid JSON that deserialises back to an object.
    /// True: downstream comparators can parse the blob without throwing.
    /// False: a malformed blob would crash subsequent HasChanges / MarkSynced calls.
    /// </summary>
    [Fact]
    public void BuildSourceMetadata_ReturnsValidJsonBlob()
    {
        var dto = MakeSourcePerson();

        var blob = PeopleSyncMergeService.BuildSourceMetadata(dto);

        Assert.NotNull(blob);
        var deserialized = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(blob);
        Assert.NotNull(deserialized);
        Assert.Equal("Source Person", deserialized["Name"].GetString());
    }

    /// <summary>
    /// Two identical inputs produce identical blob strings.
    /// True: hashes downstream are stable so SourceHash == SyncedHash actually fires.
    /// False: non-deterministic blob building breaks the short-circuit.
    /// </summary>
    [Fact]
    public void BuildSourceMetadata_StableForSameInputs()
    {
        var dto = MakeSourcePerson(d =>
        {
            d.Tags = new List<string> { "voice-actor", "veteran" };
        });

        var a = PeopleSyncMergeService.BuildSourceMetadata(dto);
        var b = PeopleSyncMergeService.BuildSourceMetadata(dto);

        Assert.Equal(a, b);
    }

    /// <summary>
    /// Tags and ProductionLocations are sorted alphabetically in the blob.
    /// True: source and local serialise identically regardless of input ordering.
    /// False: input-order differences would create permanent false-positive diffs.
    /// </summary>
    [Fact]
    public void BuildSourceMetadata_SortsTagsAndProductionLocations()
    {
        var dto = MakeSourcePerson(d =>
        {
            d.Tags = new List<string> { "zebra", "alpha", "middle" };
            d.ProductionLocations = new List<string> { "Z-place", "A-place" };
        });

        var blob = PeopleSyncMergeService.BuildSourceMetadata(dto);
        var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(blob);

        var tags = new List<string>();
        foreach (var e in d!["Tags"].EnumerateArray()) tags.Add(e.GetString()!);
        Assert.Equal(new[] { "alpha", "middle", "zebra" }, tags);

        var locations = new List<string>();
        foreach (var e in d["ProductionLocations"].EnumerateArray()) locations.Add(e.GetString()!);
        Assert.Equal(new[] { "A-place", "Z-place" }, locations);
    }

    /// <summary>
    /// Null LockData on the source defaults to false in the blob.
    /// True: missing LockData round-trips as the explicit false the local-side builder also produces.
    /// False: asymmetric null vs false would create permanent diffs on every Person row.
    /// </summary>
    [Fact]
    public void BuildSourceMetadata_LockData_DefaultsToFalseWhenNull()
    {
        var dto = MakeSourcePerson(d => d.LockData = null);

        var blob = PeopleSyncMergeService.BuildSourceMetadata(dto);
        var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(blob);

        Assert.False(d!["LockData"].GetBoolean());
    }

    /// <summary>
    /// Null source and null local return null source/local values.
    /// True: callers get nulls rather than empty strings and can short-circuit.
    /// False: empty-string outputs would non-trivially fail comparator equality.
    /// </summary>
    [Fact]
    public void PopulateImageData_NullInputs_ReturnsNulls()
    {
        var (src, loc) = PeopleSyncMergeService.PopulateImageData(null, null);

        Assert.Null(src);
        Assert.Null(loc);
    }

    /// <summary>
    /// Source-with-image and null local returns just the source-side manifest.
    /// True: source-only refresh path doesn't accidentally clear the previously-set local manifest.
    /// False: local-side blob would be wiped when local lookup fails.
    /// </summary>
    [Fact]
    public void PopulateImageData_SourceWithImageTag_LocalNull_ReturnsSourceOnly()
    {
        var dto = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "person",
            ImageTags = new BaseItemDto_ImageTags
            {
                AdditionalData = new Dictionary<string, object>
                {
                    ["Primary"] = "image-tag-abc"
                }
            }
        };

        var (src, loc) = PeopleSyncMergeService.PopulateImageData(dto, null);

        Assert.NotNull(src);
        Assert.Null(loc);
    }

    /// <summary>
    /// Several BackdropImageTags become one Backdrop entry per tag, indexed by position.
    /// True: the apply downloads and writes every source backdrop instead of just the first.
    /// False: a single-entry Backdrop manifest would silently drop the rest and never report the variance.
    /// </summary>
    [Fact]
    public void PopulateImageData_SourceWithMultipleBackdrops_EmitsOneEntryPerTag()
    {
        var dto = MakeSourcePerson(d => d.BackdropImageTags = new List<string> { "bd-0", "bd-1", "bd-2" });

        var (src, _) = PeopleSyncMergeService.PopulateImageData(dto, null);

        Assert.NotNull(src);
        var manifest = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(src!);
        var backdrops = manifest!["Backdrop"];

        Assert.Equal(3, backdrops.Count);
        Assert.Equal(new[] { 0, 1, 2 }, backdrops.ConvertAll(b => b.ImageIndex));
        Assert.Equal(new[] { "bd-0", "bd-1", "bd-2" }, backdrops.ConvertAll(b => b.Tag));
    }

    /// <summary>
    /// Backdrops and a Primary tag coexist in the same source manifest.
    /// True: a person with both syncs both, and the comparator sees each type's real count.
    /// False: one type overwriting the other would queue an apply that can never verify.
    /// </summary>
    [Fact]
    public void PopulateImageData_SourceWithPrimaryAndBackdrops_KeepsBothTypes()
    {
        var dto = MakeSourcePerson(d =>
        {
            d.ImageTags = new BaseItemDto_ImageTags
            {
                AdditionalData = new Dictionary<string, object> { ["Primary"] = "primary-tag" }
            };
            d.BackdropImageTags = new List<string> { "bd-0", "bd-1" };
        });

        var (src, _) = PeopleSyncMergeService.PopulateImageData(dto, null);

        var manifest = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(src!);

        Assert.Single(manifest!["Primary"]);
        Assert.Equal(2, manifest["Backdrop"].Count);
    }

    /// <summary>
    /// DTO without ImageTags returns null source-image manifest.
    /// True: HasImagesChanges treats null as nothing to sync.
    /// False: a non-null but empty manifest could trigger a no-op apply that then fails verify.
    /// </summary>
    [Fact]
    public void PopulateImageData_SourceWithNoImageTags_ReturnsNullSource()
    {
        var dto = MakeSourcePerson();

        var (src, _) = PeopleSyncMergeService.PopulateImageData(dto, null);

        Assert.Null(src);
    }

    /// <summary>
    /// HasChangesToSync delegates to the record's HasChanges.
    /// True: the parity helper matches the surface of the other merge services.
    /// False: divergent behaviour between helper and record's HasChanges would confuse callers.
    /// </summary>
    [Fact]
    public void HasChangesToSync_PassesThroughToItemHasChanges()
    {
        var item = MakeItem();

        Assert.False(PeopleSyncMergeService.HasChangesToSync(item));

        item.Metadata.UpdateSource("{\"a\":1}");
        Assert.True(PeopleSyncMergeService.HasChangesToSync(item));
    }

    /// <summary>
    /// Idempotent row returns "No changes".
    /// True: synced rows show the canonical no-op message.
    /// False: noisy summaries on idempotent rows mislead the operator.
    /// </summary>
    [Fact]
    public void GetChangeSummary_NoChanges_ReturnsNoChanges()
    {
        var item = MakeItem();

        Assert.Equal("No changes", PeopleSyncMergeService.GetChangeSummary(item));
    }

    /// <summary>
    /// Summary lists each changed category by name.
    /// True: operators see "Metadata, Images" rather than a generic "changes detected".
    /// False: only the first category surfaces and others are silently swallowed.
    /// </summary>
    [Fact]
    public void GetChangeSummary_ListsChangedCategories()
    {
        var item = MakeItem();
        item.Metadata.UpdateSource("{\"a\":1}");
        item.Images.UpdateSource("{\"Primary\":[{\"Size\":100,\"Tag\":\"t\"}]}");

        var summary = PeopleSyncMergeService.GetChangeSummary(item);

        Assert.Contains("Metadata", summary);
        Assert.Contains("Images", summary);
    }
}
