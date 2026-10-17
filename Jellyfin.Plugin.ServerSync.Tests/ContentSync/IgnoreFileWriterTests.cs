using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Cleanup;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.ContentSync;

public sealed class IgnoreFileWriterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ignore-" + Guid.NewGuid().ToString("N"));

    public IgnoreFileWriterTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public void HideFolder_WritesAnEmptyFile_AndUnhideRemovesIt()
    {
        Assert.True(IgnoreFileWriter.HideFolder(_folder));
        Assert.Equal(0, new FileInfo(Path.Combine(_folder, IgnoreFileWriter.FileName)).Length);
        Assert.True(IgnoreFileWriter.UnhideFolder(_folder));
        Assert.False(File.Exists(Path.Combine(_folder, IgnoreFileWriter.FileName)));
    }

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

    [Fact]
    public void TitleFolder_IsTheFirstFolderUnderTheRoot()
    {
        var root = Path.Combine(_folder, "Shows");
        var episode = Path.Combine(root, "Show", "Season 01", "Show S01E01.mkv");
        Assert.Equal(Path.Combine(root, "Show"), IgnoreFileWriter.TitleFolder(episode, root));
        Assert.Null(IgnoreFileWriter.TitleFolder(Path.Combine(root, "Loose.mkv"), root));
    }
}
