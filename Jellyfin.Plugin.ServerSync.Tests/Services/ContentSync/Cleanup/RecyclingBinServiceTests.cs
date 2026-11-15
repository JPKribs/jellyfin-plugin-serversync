using System;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Cleanup;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.ContentSync.Cleanup;

/// <summary>
/// OverlapsLibraryRoot gates both the recycling-bin retention cleanup and the
/// temp-dir cleanup, the two tasks that permanently delete stale files by
/// age. A miss here means "pointed the bin at a media folder" ends in media
/// files being deleted once they age past retention.
/// </summary>
public class RecyclingBinServiceTests
{
    private static PluginConfiguration ConfigWithRoot(string root, bool enabled = true)
    {
        var config = new PluginConfiguration();
        var server = new SourceServer { Url = "http://source:8096", ApiKey = "k" };
        server.LibraryMappings.Add(new LibraryMapping
        {
            IsEnabled = enabled,
            SourceLibraryId = "src",
            LocalLibraryId = "loc",
            LocalRootPath = root
        });
        config.Servers.Add(server);
        return config;
    }

    private static string P(params string[] parts) => System.IO.Path.Combine(parts);

    /// <summary>
    /// A recycling bin inside a library root overlaps it.
    /// True: the bin is refused, so deleted files are not scanned back into the library.
    /// False: recycled files would reappear as library content.
    /// </summary>
    [Fact]
    public void BinInsideLibrary_Overlaps()
    {
        var config = ConfigWithRoot(P("/", "media", "movies"));
        Assert.True(RecyclingBinService.OverlapsLibraryRoot(P("/", "media", "movies", "recycle"), config));
    }

    /// <summary>
    /// A recycling bin equal to a library root overlaps it.
    /// True: the bin is refused.
    /// False: the library itself could be used as the bin and emptied by age.
    /// </summary>
    [Fact]
    public void BinEqualsLibrary_Overlaps()
    {
        var config = ConfigWithRoot(P("/", "media", "movies"));
        Assert.True(RecyclingBinService.OverlapsLibraryRoot(P("/", "media", "movies"), config));
    }

    /// <summary>
    /// A library root inside the recycling bin overlaps it.
    /// True: the bin is refused.
    /// False: the age cleanup of the bin could delete a library.
    /// </summary>
    [Fact]
    public void LibraryInsideBin_Overlaps()
    {
        var config = ConfigWithRoot(P("/", "storage", "bin", "media"));
        Assert.True(RecyclingBinService.OverlapsLibraryRoot(P("/", "storage", "bin"), config));
    }

    /// <summary>
    /// A bin and a library in separate folders do not overlap.
    /// True: a sensible setup is accepted.
    /// False: every bin would be refused.
    /// </summary>
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

    /// <summary>
    /// An empty bin path overlaps nothing.
    /// True: an unset bin is not reported as a clash.
    /// False: leaving the bin unset would raise a false overlap error.
    /// </summary>
    [Fact]
    public void EmptyBinPath_NoOverlap()
    {
        var config = ConfigWithRoot(P("/", "media", "movies"));
        Assert.False(RecyclingBinService.OverlapsLibraryRoot(string.Empty, config));
    }

    /// <summary>
    /// A Recycling Bin overlaps a root folder when either holds the other or they are the same folder, and not when they only share a prefix.
    /// True: a bin inside a library, or a library inside the bin, is refused, and a neighbouring folder is allowed.
    /// False: recycled files would be scanned back in as media, or a safe folder would be refused.
    /// </summary>
    [Theory]
    [InlineData("/media/movies/.bin", true)]
    [InlineData("/media/movies", true)]
    [InlineData("/media", true)]
    [InlineData("/media/movies2", false)]
    [InlineData("/media/bin", false)]
    public void OverlapsLibraryRoot_BoundsByWholeFolderNames(string bin, bool expected)
    {
        var config = ConfigWithRoots("/media/movies");

        var overlaps = RecyclingBinService.OverlapsLibraryRoot(bin, config);

        Assert.Equal(expected, overlaps);
    }

    private static PluginConfiguration ConfigWithRoots(params string[] roots)
    {
        var server = new SourceServer { Key = "a", Url = "http://a", ApiKey = "k", IsEnabled = true };
        foreach (var root in roots)
        {
            server.LibraryMappings.Add(new LibraryMapping { SourceLibraryId = "s", LocalLibraryId = "l", SourceRootPath = "/src", LocalRootPath = root, IsEnabled = true });
        }

        var config = new PluginConfiguration();
        config.Servers.Add(server);
        return config;
    }
}
