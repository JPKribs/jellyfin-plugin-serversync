using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Downloads;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.ContentSync.Downloads;

/// <summary>
/// A temp file is reported as in use from the moment its download starts until it ends, so cleanup never deletes a file being written.
/// </summary>
public sealed class ActiveDownloadTrackerTests : IDisposable
{
    private readonly SyncTempFolder _temp = new("tracker");
    private readonly string _itemId = "item-" + Guid.NewGuid().ToString("N");

    /// <inheritdoc/>
    public void Dispose()
    {
        ActiveDownloadTracker.CompleteDownload(_itemId);
        _temp.Dispose();
    }

    /// <summary>
    /// A started download marks its temp file as in use and lists it.
    /// True: cleanup skips the real file a download is writing, and the file stays on disk.
    /// False: cleanup would delete a half written download from under the writer.
    /// </summary>
    [Fact]
    public void TryStartDownload_NewItem_MarksItsTempFileInUse()
    {
        var tempFile = _temp.Write("film.mkv.tmp");

        var started = ActiveDownloadTracker.TryStartDownload(_itemId, tempFile);

        Assert.True(started);
        Assert.True(ActiveDownloadTracker.IsTempFileInUse(tempFile));
        Assert.Contains(tempFile, ActiveDownloadTracker.GetActiveTempFiles());
        Assert.True(File.Exists(tempFile));
    }

    /// <summary>
    /// A second start for the same item is refused and the first temp file stays the one in use.
    /// True: one item downloads once at a time.
    /// False: two downloads would write the same item and the first file would lose its protection.
    /// </summary>
    [Fact]
    public void TryStartDownload_ItemAlreadyDownloading_IsRefused()
    {
        var first = _temp.Write("first.tmp");
        var second = _temp.Write("second.tmp");
        ActiveDownloadTracker.TryStartDownload(_itemId, first);

        var startedAgain = ActiveDownloadTracker.TryStartDownload(_itemId, second);

        Assert.False(startedAgain);
        Assert.True(ActiveDownloadTracker.IsTempFileInUse(first));
        Assert.False(ActiveDownloadTracker.IsTempFileInUse(second));
    }

    /// <summary>
    /// Ending a download frees its temp file and lets the item start again.
    /// True: a finished or failed download leaves nothing protected, and a retry can begin.
    /// False: the temp file could never be cleaned up and the item could never download again.
    /// </summary>
    [Fact]
    public void CompleteDownload_ActiveItem_FreesTheFileAndTheItem()
    {
        var tempFile = _temp.Write("film.mkv.tmp");
        ActiveDownloadTracker.TryStartDownload(_itemId, tempFile);

        ActiveDownloadTracker.CompleteDownload(_itemId);

        Assert.False(ActiveDownloadTracker.IsTempFileInUse(tempFile));
        Assert.DoesNotContain(tempFile, ActiveDownloadTracker.GetActiveTempFiles());
        Assert.True(ActiveDownloadTracker.TryStartDownload(_itemId, tempFile));
    }

    /// <summary>
    /// Ending a download that was never started does nothing.
    /// True: a cleanup path can always call it.
    /// False: a failed start followed by the usual cleanup would throw.
    /// </summary>
    [Fact]
    public void CompleteDownload_UnknownItem_DoesNothing()
    {
        var exception = Record.Exception(() => ActiveDownloadTracker.CompleteDownload(_itemId));

        Assert.Null(exception);
    }

    /// <summary>
    /// The in use check ignores the case of the path.
    /// True: a path spelled in another case on a case blind filesystem is still protected.
    /// False: cleanup would delete an active temp file it reached under another spelling.
    /// </summary>
    [Fact]
    public void IsTempFileInUse_PathInAnotherCase_StillMatches()
    {
        var tempFile = _temp.Write("Film.MKV.tmp");
        ActiveDownloadTracker.TryStartDownload(_itemId, tempFile);

        var inUse = ActiveDownloadTracker.IsTempFileInUse(tempFile.ToUpperInvariant());

        Assert.True(inUse);
    }

    /// <summary>
    /// A file no download registered, a sibling whose name shares the active file's prefix, and the folder itself are not in use.
    /// True: only the exact temp file is protected, so stale neighbors are still cleaned up.
    /// False: one active download would shield every file that starts with its name.
    /// </summary>
    [Fact]
    public void IsTempFileInUse_OtherPaths_AreNotInUse()
    {
        var tempFile = _temp.Write("film.tmp");
        var sibling = _temp.Write("film.tmp2");
        ActiveDownloadTracker.TryStartDownload(_itemId, tempFile);

        Assert.False(ActiveDownloadTracker.IsTempFileInUse(sibling));
        Assert.False(ActiveDownloadTracker.IsTempFileInUse(_temp.Root));
        Assert.False(ActiveDownloadTracker.IsTempFileInUse(_temp.PathOf("never-registered.tmp")));
    }

    /// <summary>
    /// A download registered with an empty temp path is tracked but lists no file.
    /// True: the list of files to protect never holds an empty path.
    /// False: cleanup would compare every file against an empty path.
    /// </summary>
    [Fact]
    public void GetActiveTempFiles_DownloadWithEmptyPath_ListsNoFileForIt()
    {
        var started = ActiveDownloadTracker.TryStartDownload(_itemId, string.Empty);

        var files = ActiveDownloadTracker.GetActiveTempFiles();

        Assert.True(started);
        Assert.DoesNotContain(string.Empty, files);
        Assert.False(ActiveDownloadTracker.TryStartDownload(_itemId, _temp.PathOf("later.tmp")));
    }

    /// <summary>
    /// The stale sweep leaves a download that just started.
    /// True: a running download keeps its protection through a sweep.
    /// False: every sweep would unprotect the downloads in flight.
    /// </summary>
    [Fact]
    public void CleanupStaleEntries_FreshDownload_IsKept()
    {
        var tempFile = _temp.Write("film.mkv.tmp");
        ActiveDownloadTracker.TryStartDownload(_itemId, tempFile);

        ActiveDownloadTracker.CleanupStaleEntries();

        Assert.True(ActiveDownloadTracker.IsTempFileInUse(tempFile));
    }

    /// <summary>
    /// A missing item id is refused on start and on end.
    /// True: a caller with no id fails at once with the argument named.
    /// False: a null key would surface as an error from inside the dictionary.
    /// </summary>
    [Fact]
    public void StartAndComplete_NullItemId_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => ActiveDownloadTracker.TryStartDownload(null!, _temp.PathOf("a.tmp")));
        Assert.Throws<ArgumentNullException>(() => ActiveDownloadTracker.CompleteDownload(null!));
    }
}
