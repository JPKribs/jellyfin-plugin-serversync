using System;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.ContentSync;

/// <summary>
/// OverlapsLibraryRoot gates both the recycling-bin retention cleanup and the
/// temp-dir cleanup — the two tasks that permanently delete stale files by
/// age. A miss here means "pointed the bin at a media folder" ends in media
/// files being deleted once they age past retention.
/// </summary>
public class RecyclingBinServiceTests
{
    private static PluginConfiguration ConfigWithRoot(string root, bool enabled = true)
    {
        var config = new PluginConfiguration();
        config.LibraryMappings.Add(new LibraryMapping
        {
            IsEnabled = enabled,
            SourceLibraryId = "src",
            LocalLibraryId = "loc",
            LocalRootPath = root
        });
        return config;
    }

    private static string P(params string[] parts) => System.IO.Path.Combine(parts);

    [Fact]
    public void BinInsideLibrary_Overlaps()
    {
        var config = ConfigWithRoot(P("/", "media", "movies"));
        Assert.True(RecyclingBinService.OverlapsLibraryRoot(P("/", "media", "movies", "recycle"), config));
    }

    [Fact]
    public void BinEqualsLibrary_Overlaps()
    {
        var config = ConfigWithRoot(P("/", "media", "movies"));
        Assert.True(RecyclingBinService.OverlapsLibraryRoot(P("/", "media", "movies"), config));
    }

    [Fact]
    public void LibraryInsideBin_Overlaps()
    {
        var config = ConfigWithRoot(P("/", "storage", "bin", "media"));
        Assert.True(RecyclingBinService.OverlapsLibraryRoot(P("/", "storage", "bin"), config));
    }

    [Fact]
    public void DisjointPaths_NoOverlap()
    {
        var config = ConfigWithRoot(P("/", "media", "movies"));
        Assert.False(RecyclingBinService.OverlapsLibraryRoot(P("/", "storage", "recycle"), config));
    }

    /// <summary>
    /// The prefix trap again: "/media/movies-recycle" is NOT inside
    /// "/media/movies" and must not be blocked (false positives make users
    /// disable the safety feature).
    /// </summary>
    [Fact]
    public void SiblingWithSharedPrefix_NoOverlap()
    {
        var config = ConfigWithRoot(P("/", "media", "movies"));
        Assert.False(RecyclingBinService.OverlapsLibraryRoot(P("/", "media", "movies-recycle"), config));
    }

    /// <summary>
    /// Disabled mappings still count: their files are still on disk, and the
    /// cleanup task deletes by age regardless of mapping state.
    /// </summary>
    [Fact]
    public void DisabledMappingRoot_StillOverlaps()
    {
        var config = ConfigWithRoot(P("/", "media", "movies"), enabled: false);
        Assert.True(RecyclingBinService.OverlapsLibraryRoot(P("/", "media", "movies", "recycle"), config));
    }

    [Fact]
    public void EmptyBinPath_NoOverlap()
    {
        var config = ConfigWithRoot(P("/", "media", "movies"));
        Assert.False(RecyclingBinService.OverlapsLibraryRoot(string.Empty, config));
    }
}
