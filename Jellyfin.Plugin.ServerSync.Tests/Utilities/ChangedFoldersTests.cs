using System;
using System.Collections.Generic;
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

    /// <summary>
    /// A folder reaches a library when Jellyfin knows it or any folder above it.
    /// True: a new title folder inside a scanned library is refreshed on its own.
    /// False: every new title folder, which Jellyfin cannot know yet, would force a full scan.
    /// </summary>
    [Fact]
    public void AllReachable_NewFolderUnderAKnownLibrary_IsReachable()
    {
        var root = P("/media", "Movies");
        Assert.True(ChangedFolders.AllReachable(new[] { P(root, "New Film (2024)") }, p => p == root));
    }

    /// <summary>
    /// A folder with no known folder above it does not reach a library, and one such folder is enough.
    /// True: a first run into an empty library, whose folder Jellyfin has not recorded, falls back to a scan.
    /// False: Jellyfin is told about a folder it drops silently, and the files wait for the next scheduled scan.
    /// </summary>
    [Fact]
    public void AllReachable_AnyFolderWithNoKnownParent_IsNotReachable()
    {
        var movies = P("/media", "Movies");
        var shows = P("/media", "Shows");
        var asked = new List<string>();
        var reachable = ChangedFolders.AllReachable(new[] { P(movies, "Film"), P(shows, "Show") }, p =>
        {
            asked.Add(p);
            return p == movies;
        });

        Assert.False(reachable);
        Assert.Contains(shows, asked);
        Assert.Contains(Path.GetPathRoot(shows)!, asked);
    }

    [Fact]
    public void AllReachable_NoFolders_IsReachable()
    {
        Assert.True(ChangedFolders.AllReachable(Array.Empty<string>(), _ => false));
    }
}
