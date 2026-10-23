using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.ContentSync;

public class DiskSpaceServiceTests
{
    /// <summary>
    /// A target whose folders do not exist yet is measured on the nearest folder that does.
    /// True: the check reads the real free space of the mount the file will land on.
    /// False: a missing folder reads as unknown, or the root is measured instead.
    /// </summary>
    [Fact]
    public void HasSufficientSpaceForFile_MissingFolders_MeasuresNearestExistingFolder()
    {
        var target = Path.Combine(Path.GetTempPath(), "serversync-missing", "a", "b", "movie.mkv");

        Assert.True(DiskSpaceService.HasSufficientSpaceForFile(target, 1, 0));
        Assert.False(DiskSpaceService.HasSufficientSpaceForFile(target, long.MaxValue / 4, 0));
    }

    /// <summary>
    /// Two downloads that each fit alone but not together do not both start. The second waits for the
    /// first to finish, then goes ahead.
    /// True: parallel downloads cannot together fill the disk.
    /// False: each passes its own check and the disk runs out partway through both.
    /// </summary>
    [Fact]
    public async Task ReserveForFile_SecondFileWaitsForTheFirst()
    {
        var target = Path.Combine(Path.GetTempPath(), "serversync-reserve", "movie.mkv");
        var free = new DriveInfo(Path.GetTempPath()).AvailableFreeSpace;
        var size = (long)(free * 0.6);

        var first = await DiskSpaceService.ReserveForFileAsync(target, size, 0, CancellationToken.None);
        Assert.NotNull(first);

        var second = DiskSpaceService.ReserveForFileAsync(target, size, 0, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(second.IsCompleted);

        first!.Dispose();
        using var hold = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(hold);
    }

    /// <summary>
    /// A file larger than the free space gets no hold and does not wait.
    /// True: it fails at once with insufficient space, as before.
    /// False: it would wait forever for space that never comes.
    /// </summary>
    [Fact]
    public async Task ReserveForFile_FileThatCanNeverFit_IsRefusedAtOnce()
    {
        var target = Path.Combine(Path.GetTempPath(), "serversync-reserve", "huge.mkv");

        var hold = await DiskSpaceService.ReserveForFileAsync(target, long.MaxValue / 4, 0, CancellationToken.None);

        Assert.Null(hold);
    }
}
