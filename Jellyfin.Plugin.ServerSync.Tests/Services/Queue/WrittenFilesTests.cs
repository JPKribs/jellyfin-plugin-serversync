using System;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// A file a sync wrote is remembered under either path separator, and nothing else is.
/// </summary>
public sealed class WrittenFilesTests
{
    /// <summary>
    /// A path with no value is never remembered.
    /// True: an item without a path is not mistaken for a file a sync wrote.
    /// False: every pathless item is silenced, or the mark throws inside a download.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Mark_NoPath_RemembersNothing(string? path)
    {
        WrittenFiles.Mark(path);

        Assert.False(WrittenFiles.WasWritten(path));
    }

    /// <summary>
    /// A file marked with one separator is found with the other.
    /// True: a file written on Windows is recognised when Jellyfin reports it with forward slashes.
    /// False: the download is announced to every peer as a new file.
    /// </summary>
    [Fact]
    public void WasWritten_OtherSeparator_FindsTheFile()
    {
        var name = Guid.NewGuid().ToString("N");
        WrittenFiles.Mark("D:\\Media\\Movies\\" + name + ".mkv");

        var found = WrittenFiles.WasWritten("D:/Media/Movies/" + name + ".mkv");

        Assert.True(found);
    }

    /// <summary>
    /// A file that was never marked is not remembered.
    /// True: a file an administrator adds by hand is announced.
    /// False: new files are silently kept from the pool.
    /// </summary>
    [Fact]
    public void WasWritten_NeverMarked_IsFalse()
    {
        var path = "/local/movies/" + Guid.NewGuid().ToString("N") + ".mkv";

        var found = WrittenFiles.WasWritten(path);

        Assert.False(found);
    }
}
