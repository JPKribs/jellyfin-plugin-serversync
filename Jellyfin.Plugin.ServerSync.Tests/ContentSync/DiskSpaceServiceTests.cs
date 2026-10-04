using System.IO;
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
}
