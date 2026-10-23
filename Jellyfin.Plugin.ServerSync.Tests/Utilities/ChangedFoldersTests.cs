using System.IO;
using Jellyfin.Plugin.ServerSync.Utilities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

/// <summary>
/// The folders a content run reports to Jellyfin. Too few and a new file is never picked up, too many
/// and the targeted refresh costs more than the full scan it replaced.
/// </summary>
public class ChangedFoldersTests
{
    private static string P(params string[] parts) => Path.Combine(parts);

    [Fact]
    public void ManyFilesInOneFolder_AreOneFolder()
    {
        var season = P("/media", "TV", "Show", "Season 01");
        var folders = ChangedFolders.For(new[] { P(season, "e01.mkv"), P(season, "e02.mkv"), P(season, "e02.en.srt") });

        Assert.Equal(new[] { season }, folders);
    }

    [Fact]
    public void FolderInsideAnotherReportedFolder_IsCoveredByIt()
    {
        var show = P("/media", "TV", "Show");
        var folders = ChangedFolders.For(new[] { P(show, "Season 01", "e01.mkv"), P(show, "poster.jpg") });

        Assert.Equal(new[] { show }, folders);
    }

    [Fact]
    public void SiblingWithACommonPrefix_IsNotTreatedAsInside()
    {
        var a = P("/media", "Movies", "Alien");
        var b = P("/media", "Movies", "Aliens");
        var folders = ChangedFolders.For(new[] { P(a, "a.mkv"), P(b, "b.mkv") });

        Assert.Equal(2, folders.Count);
    }

    [Fact]
    public void BlankPaths_AreIgnored()
    {
        Assert.Empty(ChangedFolders.For(new[] { string.Empty, "  " }));
    }
}
