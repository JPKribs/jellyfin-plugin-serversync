using System;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Queue;

public class HintMappingTests
{
    private static readonly Guid LocalUser = Guid.NewGuid();
    private static readonly Guid PeerUser = Guid.NewGuid();

    private static SourceServer Peer(bool enabled = true) => new()
    {
        Name = "peer",
        LibraryMappings =
        {
            new LibraryMapping { SourceRootPath = "/peer/Movies", LocalRootPath = "/local/Movies", IsEnabled = enabled },
            new LibraryMapping { SourceRootPath = "/peer/Shows", LocalRootPath = "/local/Shows", IsEnabled = true }
        },
        UserMappings =
        {
            new UserMapping { SourceUserId = PeerUser.ToString("N"), LocalUserId = LocalUser.ToString("N"), IsEnabled = enabled }
        }
    };

    /// <summary>
    /// The sending side matches local ids and local roots, the receiving side source ids and source roots.
    /// True: a hint and a scan land on the same local object.
    /// False: a hint could apply to the wrong user or library.
    /// </summary>
    [Fact]
    public void FindsMappingsOnBothSides()
    {
        var peer = Peer();
        Assert.NotNull(HintMapping.FindByLocalUser(peer, LocalUser));
        Assert.Null(HintMapping.FindByLocalUser(peer, PeerUser));
        Assert.Equal("/peer/Movies", HintMapping.FindByLocalPath(peer, "/local/Movies/A (2020)/A.mp4")?.SourceRootPath);
        Assert.Null(HintMapping.FindByLocalPath(peer, "/local/Music/song.flac"));

        Assert.NotNull(HintMapping.FindBySourceUser(peer, PeerUser));
        Assert.Null(HintMapping.FindBySourceUser(peer, LocalUser));
        Assert.Equal("/local/Shows", HintMapping.FindBySourcePath(peer, "/peer/Shows/S/Season 01/e.mp4")?.LocalRootPath);
    }

    /// <summary>
    /// Disabled mappings do not carry hints.
    /// True: switching a mapping off stops both the scan and the hints for it.
    /// False: a disabled library would keep syncing through hints.
    /// </summary>
    [Fact]
    public void DisabledMappings_AreIgnored()
    {
        var peer = Peer(enabled: false);
        Assert.Null(HintMapping.FindByLocalUser(peer, LocalUser));
        Assert.Null(HintMapping.FindByLocalPath(peer, "/local/Movies/A/A.mp4"));
    }

    /// <summary>
    /// Root matching is by whole path segment, in either separator and any case.
    /// True: a library at /media/Movies does not claim /media/Movies2.
    /// False: a sibling folder with a shared prefix would route to the wrong library.
    /// </summary>
    [Theory]
    [InlineData("/media/Movies/A/a.mp4", "/media/Movies", true)]
    [InlineData("/media/Movies", "/media/Movies/", true)]
    [InlineData("/media/Movies2/A/a.mp4", "/media/Movies", false)]
    [InlineData("C:\\Media\\Movies\\a.mp4", "c:/media/movies", true)]
    [InlineData("/x", "", false)]
    public void IsUnder_MatchesSegments(string path, string root, bool expected)
    {
        Assert.Equal(expected, HintMapping.IsUnder(path, root));
    }
}
