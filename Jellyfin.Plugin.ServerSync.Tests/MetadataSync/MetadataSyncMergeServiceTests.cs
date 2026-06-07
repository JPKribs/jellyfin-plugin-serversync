using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Sdk.Generated.Models;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.MetadataSync;

public class MetadataSyncMergeServiceTests
{
    private static MetadataSyncItem MakeItem(string localItemId = "local-1") => new()
    {
        SourceLibraryId = "src-lib",
        LocalLibraryId = "loc-lib",
        SourceItemId = "src-item",
        LocalItemId = localItemId,
        ItemName = "Test Item"
    };

    private static BaseItemDto MakeSourceDto(System.Action<BaseItemDto>? configure = null)
    {
        var dto = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Source Name",
            OriginalTitle = "Original",
            Overview = "Source overview"
        };
        configure?.Invoke(dto);
        return dto;
    }

    /// <summary>
    /// Source-side metadata blob is populated from BaseItemDto.
    /// True: Name / OriginalTitle / Overview round-trip into item.Metadata.Source.
    /// False: extracted source data is missing fields the apply path expects.
    /// </summary>
    [Fact]
    public void MergeMetadataFields_PopulatesSourceBlob_FromBaseItemDto()
    {
        var item = MakeItem();
        var dto = MakeSourceDto();

        MetadataSyncMergeService.MergeMetadataFields(item, dto, localItem: null, syncGenres: false, syncTags: false);

        Assert.NotNull(item.Metadata.Source);
        var blob = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.Metadata.Source!);
        Assert.NotNull(blob);
        Assert.Equal("Source Name", blob["Name"].GetString());
        Assert.Equal("Original", blob["OriginalTitle"].GetString());
        Assert.Equal("Source overview", blob["Overview"].GetString());
    }

    /// <summary>
    /// Genres are omitted from the source blob when syncGenres is false.
    /// True: disabled-by-config genres don't appear in the blob and the hash doesn't include them.
    /// False: genres would leak into the hash regardless of config, breaking the config-toggle flow.
    /// </summary>
    [Fact]
    public void MergeMetadataFields_SyncGenresFalse_OmitsGenresField()
    {
        var item = MakeItem();
        var dto = MakeSourceDto(d => d.Genres = new List<string> { "Drama", "Action" });

        MetadataSyncMergeService.MergeMetadataFields(item, dto, localItem: null, syncGenres: false, syncTags: false);

        var blob = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.Metadata.Source!);
        Assert.False(blob!.ContainsKey("Genres"));
    }

    /// <summary>
    /// Genres are included in the source blob when syncGenres is true.
    /// True: enabled-by-config genres appear in the blob and contribute to the hash.
    /// False: enabling syncGenres would have no effect on what's compared.
    /// </summary>
    [Fact]
    public void MergeMetadataFields_SyncGenresTrue_IncludesNormalizedGenres()
    {
        var item = MakeItem();
        var dto = MakeSourceDto(d => d.Genres = new List<string> { "Drama", "Action" });

        MetadataSyncMergeService.MergeMetadataFields(item, dto, localItem: null, syncGenres: true, syncTags: false);

        var blob = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.Metadata.Source!);
        Assert.True(blob!.ContainsKey("Genres"));
    }

    /// <summary>
    /// syncTags toggles whether Tags are included in the source blob.
    /// True: the flag controls Tags symmetrically with syncGenres.
    /// False: Tags would be unconditionally synced or unconditionally skipped.
    /// </summary>
    [Fact]
    public void MergeMetadataFields_SyncTagsToggleControlsTagsField()
    {
        var item = MakeItem();
        var dto = MakeSourceDto(d => d.Tags = new List<string> { "tag1", "tag2" });

        MetadataSyncMergeService.MergeMetadataFields(item, dto, localItem: null, syncGenres: false, syncTags: false);
        var blobOff = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.Metadata.Source!);
        Assert.False(blobOff!.ContainsKey("Tags"));

        MetadataSyncMergeService.MergeMetadataFields(item, dto, localItem: null, syncGenres: false, syncTags: true);
        var blobOn = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.Metadata.Source!);
        Assert.True(blobOn!.ContainsKey("Tags"));
    }

    /// <summary>
    /// MergeMetadataFields populates Metadata.SourceHash.
    /// True: the SourceHash is non-empty after a successful blob extraction.
    /// False: the hash short-circuit can never fire on Metadata rows.
    /// </summary>
    [Fact]
    public void MergeMetadataFields_RecomputesSourceHash()
    {
        var item = MakeItem();
        var dto = MakeSourceDto();

        MetadataSyncMergeService.MergeMetadataFields(item, dto, localItem: null, syncGenres: false, syncTags: false);

        Assert.NotNull(item.Metadata.SourceHash);
        Assert.NotEmpty(item.Metadata.SourceHash);
    }

    /// <summary>
    /// Two identical DTO inputs produce identical SourceHash values.
    /// True: hash is reproducible across refreshes so SourceHash == SyncedHash actually fires.
    /// False: non-deterministic hashing breaks the short-circuit on Metadata rows.
    /// </summary>
    [Fact]
    public void MergeMetadataFields_ProducesStableHash_ForIdenticalInputs()
    {
        var dto = MakeSourceDto(d => d.ProductionYear = 2025);

        var a = MakeItem();
        MetadataSyncMergeService.MergeMetadataFields(a, dto, localItem: null, syncGenres: true, syncTags: true);

        var b = MakeItem();
        MetadataSyncMergeService.MergeMetadataFields(b, dto, localItem: null, syncGenres: true, syncTags: true);

        Assert.Equal(a.Metadata.SourceHash, b.Metadata.SourceHash);
    }

    /// <summary>
    /// Toggling syncGenres changes the SourceHash for the same DTO.
    /// True: flipping the config flag forces a re-queue on the next refresh.
    /// False: config changes wouldn't trigger a re-sync; rows would stay Synced with stale state.
    /// </summary>
    [Fact]
    public void MergeMetadataFields_ProducesDifferentHash_ForDifferentGenreFlags()
    {
        var dto = MakeSourceDto(d => d.Genres = new List<string> { "Drama" });

        var withGenres = MakeItem();
        MetadataSyncMergeService.MergeMetadataFields(withGenres, dto, localItem: null, syncGenres: true, syncTags: false);

        var withoutGenres = MakeItem();
        MetadataSyncMergeService.MergeMetadataFields(withoutGenres, dto, localItem: null, syncGenres: false, syncTags: false);

        Assert.NotEqual(withGenres.Metadata.SourceHash, withoutGenres.Metadata.SourceHash);
    }

    /// <summary>
    /// No source studios yields an empty-array Studios.Source.
    /// True: HasStudiosChanges correctly treats "[]" as nothing to sync.
    /// False: null Studios.Source would either crash the apply or wrongly diff against local.
    /// </summary>
    [Fact]
    public void MergeStudios_NullSourceStudios_SetsEmptyArray()
    {
        var item = MakeItem();
        var dto = MakeSourceDto();

        MetadataSyncMergeService.MergeStudios(item, dto, localItem: null);

        Assert.Equal("[]", item.Studios.Source);
    }

    /// <summary>
    /// Studio names with only whitespace are filtered from the source blob.
    /// True: whitespace-only studios drop out symmetrically with local-side filtering.
    /// False: asymmetric filtering would create permanent false-positive diffs on those rows.
    /// </summary>
    [Fact]
    public void MergeStudios_FiltersWhitespaceNames()
    {
        var item = MakeItem();
        var dto = MakeSourceDto(d => d.Studios = new List<NameGuidPair>
        {
            new() { Name = "Studio A", Id = Guid.NewGuid() },
            new() { Name = "  ", Id = Guid.NewGuid() },
            new() { Name = "Studio B", Id = Guid.NewGuid() }
        });

        MetadataSyncMergeService.MergeStudios(item, dto, localItem: null);

        var deserialized = JsonSerializer.Deserialize<List<string>>(item.Studios.Source!);
        Assert.Equal(new[] { "Studio A", "Studio B" }, deserialized);
    }

    /// <summary>
    /// Studios are sorted alphabetically before serialization.
    /// True: stable ordering means source and local serialize identically and don't false-positive.
    /// False: unstable ordering would mark every Studios row as diff on every refresh.
    /// </summary>
    [Fact]
    public void MergeStudios_SortsAlphabetically()
    {
        var item = MakeItem();
        var dto = MakeSourceDto(d => d.Studios = new List<NameGuidPair>
        {
            new() { Name = "Zebra", Id = Guid.NewGuid() },
            new() { Name = "Alpha", Id = Guid.NewGuid() }
        });

        MetadataSyncMergeService.MergeStudios(item, dto, localItem: null);

        var deserialized = JsonSerializer.Deserialize<List<string>>(item.Studios.Source!);
        Assert.Equal(new[] { "Alpha", "Zebra" }, deserialized);
    }

    /// <summary>
    /// Null local item leaves Studios.Local untouched.
    /// True: the source-only refresh path doesn't clobber a previously-populated Local.
    /// False: callers without a local correlate would wipe the local blob.
    /// </summary>
    [Fact]
    public void MergeStudios_NullLocalItem_LeavesLocalStudiosNull()
    {
        var item = MakeItem();
        var dto = MakeSourceDto(d => d.Studios = new List<NameGuidPair>
        {
            new() { Name = "A", Id = Guid.NewGuid() }
        });

        MetadataSyncMergeService.MergeStudios(item, dto, localItem: null);

        Assert.Null(item.Studios.Local);
    }

    /// <summary>
    /// No source people yields an empty-array People.Source.
    /// True: HasPeopleChanges correctly treats "[]" as nothing to sync.
    /// False: null People.Source would either crash the apply or wrongly diff against local.
    /// </summary>
    [Fact]
    public void MergePeople_NoSourcePeople_SetsEmptyArray()
    {
        var item = MakeItem();
        var dto = MakeSourceDto();

        MetadataSyncMergeService.MergePeople(item, dto, localItem: null, libraryManager: null!);

        Assert.Equal("[]", item.People.Source);
    }

    /// <summary>
    /// Null local item leaves People.Local untouched.
    /// True: source-only refresh path doesn't clobber a previously-populated Local.
    /// False: callers without a local correlate would wipe the local blob.
    /// </summary>
    [Fact]
    public void MergePeople_NullLocalItem_LeavesLocalPeopleNull()
    {
        var item = MakeItem();
        var dto = MakeSourceDto(d => d.People = new List<BaseItemPerson>
        {
            new() { Name = "Actor A", Role = "Lead" }
        });

        MetadataSyncMergeService.MergePeople(item, dto, localItem: null, libraryManager: null!);

        Assert.Null(item.People.Local);
    }

    /// <summary>
    /// HasChangesToSync delegates to the record's HasChanges.
    /// True: callers get one entry point matching the surface of the other merge services.
    /// False: divergent behaviour between the helper and the record's HasChanges would confuse callers.
    /// </summary>
    [Fact]
    public void HasChangesToSync_PassesThroughToItemHasChanges()
    {
        var item = MakeItem();

        Assert.False(MetadataSyncMergeService.HasChangesToSync(item));

        item.Metadata.UpdateSource("{\"a\":1}");
        Assert.True(MetadataSyncMergeService.HasChangesToSync(item));
    }

    /// <summary>
    /// Idempotent row returns the "No changes" sentinel.
    /// True: synced rows display the canonical no-op message in summaries.
    /// False: noisy summaries on idempotent rows mislead the operator.
    /// </summary>
    [Fact]
    public void GetChangeSummary_NoChanges_ReturnsNoChanges()
    {
        var item = MakeItem();

        Assert.Equal("No changes", MetadataSyncMergeService.GetChangeSummary(item));
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

        var summary = MetadataSyncMergeService.GetChangeSummary(item);

        Assert.Contains("Metadata", summary);
        Assert.Contains("Images", summary);
    }
}
