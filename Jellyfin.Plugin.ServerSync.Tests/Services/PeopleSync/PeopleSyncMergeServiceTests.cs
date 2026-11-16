using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.PeopleSync;
using Jellyfin.Sdk.Generated.Models;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.PeopleSync;

/// <summary>
/// The people merge service builds a person's metadata blob and image data from the source.
/// </summary>
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
        // Name is the match key and is never synced.
        Assert.False(deserialized.ContainsKey("Name"));
        Assert.True(deserialized.ContainsKey("Overview"));
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
}
