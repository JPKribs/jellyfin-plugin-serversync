using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Cleanup;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.ContentSync.Cleanup;

/// <summary>
/// The ignore file writer hides a title folder from Jellyfin while a sync writes into it, and never touches a file it did not write.
/// </summary>
public sealed class IgnoreFileWriterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ignore-" + Guid.NewGuid().ToString("N"));

    public IgnoreFileWriterTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    /// <summary>
    /// Hiding a folder writes an empty ignore file, and unhiding removes it.
    /// True: Jellyfin skips the folder while it is hidden and sees it again after.
    /// False: a folder being synced would be scanned half written, or stay hidden for good.
    /// </summary>
    [Fact]
    public void HideFolder_WritesAnEmptyFile_AndUnhideRemovesIt()
    {
        Assert.True(IgnoreFileWriter.HideFolder(_folder));
        Assert.Equal(0, new FileInfo(Path.Combine(_folder, IgnoreFileWriter.FileName)).Length);
        Assert.True(IgnoreFileWriter.UnhideFolder(_folder));
        Assert.False(File.Exists(Path.Combine(_folder, IgnoreFileWriter.FileName)));
    }

    /// <summary>
    /// A folder that already has an ignore file keeps it, and is not unhidden by this writer.
    /// True: the user's own rules survive a sync, and the writer reports it did nothing.
    /// False: the writer would overwrite or delete an ignore file it never wrote.
    /// </summary>
    [Fact]
    public void HideFolder_LeavesAnExistingIgnoreFileAlone()
    {
        var path = Path.Combine(_folder, IgnoreFileWriter.FileName);
        File.WriteAllText(path, "*.nfo");
        Assert.False(IgnoreFileWriter.HideFolder(_folder));
        Assert.Equal("*.nfo", File.ReadAllText(path));
        Assert.False(IgnoreFileWriter.UnhideFolder(_folder));
        Assert.True(File.Exists(path));
    }

    /// <summary>
    /// The title folder of a file is the first folder under its library root.
    /// True: an episode resolves to its show folder, and a file directly in the root has none.
    /// False: the wrong folder, or the whole library root, would be hidden during a sync.
    /// </summary>
    [Fact]
    public void TitleFolder_IsTheFirstFolderUnderTheRoot()
    {
        var root = Path.Combine(_folder, "Shows");
        var episode = Path.Combine(root, "Show", "Season 01", "Show S01E01.mkv");
        Assert.Equal(Path.Combine(root, "Show"), IgnoreFileWriter.TitleFolder(episode, root));
        Assert.Null(IgnoreFileWriter.TitleFolder(Path.Combine(root, "Loose.mkv"), root));
    }
}
