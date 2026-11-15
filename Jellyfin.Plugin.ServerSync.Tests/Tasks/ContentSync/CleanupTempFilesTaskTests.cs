using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Downloads;
using Jellyfin.Plugin.ServerSync.Tasks.ContentSync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Tasks.ContentSync;

/// <summary>
/// The temp cleanup deletes only files a failed download left behind for more than a day, and never a file in use or a file outside the temp folders it owns.
/// </summary>
public sealed class CleanupTempFilesTaskTests : IDisposable
{
    private readonly string _root;
    private readonly string _temp;
    private readonly FakeConfigManager _config;

    public CleanupTempFilesTaskTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "serversync-cleanup-" + Guid.NewGuid().ToString("N"));
        _temp = Path.Combine(_root, "temp");
        Directory.CreateDirectory(_temp);
        _config = new FakeConfigManager(_temp);
        _config.Configuration.EnableContentSync = true;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private CleanupTempFilesTask Task() => new(NullLogger<CleanupTempFilesTask>.Instance, _config);

    private string File(string relativePath, double ageHours)
    {
        var path = Path.Combine(_temp, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "partial");
        System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-ageHours));
        return path;
    }

    /// <summary>
    /// A file unwritten for more than a day is deleted, and one written within the day is kept.
    /// True: abandoned downloads are removed while a slow download in progress survives.
    /// False: the temp folder fills the disk, or a running download loses its file.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_OldAndRecentFiles_DeletesOnlyTheOld()
    {
        var old = File("old.tmp", ageHours: 48);
        var recent = File("recent.tmp", ageHours: 1);

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.False(System.IO.File.Exists(old));
        Assert.True(System.IO.File.Exists(recent));
    }

    /// <summary>
    /// Old files in the metadata and people image folders are deleted, and old files in any other subfolder are kept.
    /// True: a crashed image apply is cleaned up without touching folders the task does not own.
    /// False: image leftovers stay forever, or an unrelated subfolder is emptied.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Subfolders_CleansOnlyTheImageFolders()
    {
        var metadataImage = File(Path.Combine("metadata-images", "a"), ageHours: 48);
        var personImage = File(Path.Combine("person-images", "b"), ageHours: 48);
        var other = File(Path.Combine("someone-else", "c"), ageHours: 48);

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.False(System.IO.File.Exists(metadataImage));
        Assert.False(System.IO.File.Exists(personImage));
        Assert.True(System.IO.File.Exists(other));
    }

    /// <summary>
    /// Every sync type that stages files being off skips the cleanup, and any one of them being on runs it.
    /// True: the task follows the sync types that use the temp folder.
    /// False: image leftovers are never cleaned when only Metadata or People is on.
    /// </summary>
    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public async Task ExecuteAsync_EnabledSyncTypes_DecideWhetherItRuns(bool content, bool metadata, bool people, bool fileKept)
    {
        _config.Configuration.EnableContentSync = content;
        _config.Configuration.EnableMetadataSync = metadata;
        _config.Configuration.EnablePeopleSync = people;
        var old = File("old.tmp", ageHours: 48);

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.Equal(fileKept, System.IO.File.Exists(old));
    }

    /// <summary>
    /// A temp folder that overlaps a library root is never cleaned.
    /// True: a misconfigured temp path cannot delete media.
    /// False: every media file older than a day in that library is deleted.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TempPathOverlapsLibraryRoot_DeletesNothing()
    {
        var old = File("movie.mkv", ageHours: 48);
        var server = new SourceServer { Key = "a", Url = "http://a", ApiKey = "k" };
        server.LibraryMappings.Add(new LibraryMapping { SourceLibraryId = "lib", LocalRootPath = _root });
        _config.Configuration.Servers.Add(server);

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.True(System.IO.File.Exists(old));
    }

    /// <summary>
    /// A file a running download holds is kept however old it looks.
    /// True: a download that stalls past a day keeps its partial file.
    /// False: the writer is left with a deleted file and the download fails.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ActiveDownload_IsKept()
    {
        var active = File("active.tmp", ageHours: 48);
        var id = "cleanup-test-" + Guid.NewGuid().ToString("N");
        Assert.True(ActiveDownloadTracker.TryStartDownload(id, active));

        try
        {
            await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);
        }
        finally
        {
            ActiveDownloadTracker.CompleteDownload(id);
        }

        Assert.True(System.IO.File.Exists(active));
    }

    /// <summary>
    /// A cancelled token stops the pass before any file is deleted, without an error.
    /// True: stopping the task leaves the remaining files for the next run.
    /// False: a stopped task keeps deleting, or ends as failed.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CancelledToken_DeletesNothing()
    {
        var old = File("old.tmp", ageHours: 48);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Task().ExecuteAsync(new TaskProgress(), cts.Token);

        Assert.True(System.IO.File.Exists(old));
    }

    /// <summary>
    /// Progress rises with each file and ends at 100.
    /// True: the task's bar reaches the end when the last file is handled.
    /// False: the bar stays short of done or jumps backwards.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SeveralFiles_ReportsProgressUpToOneHundred()
    {
        File("a.tmp", ageHours: 48);
        File("b.tmp", ageHours: 1);
        var progress = new TaskProgress();

        await Task().ExecuteAsync(progress, CancellationToken.None);

        Assert.Equal(new[] { 50.0, 100.0 }, progress.Values);
    }

    /// <summary>
    /// A missing or empty temp folder is a quiet no-op.
    /// True: a server that never downloaded anything runs the task cleanly.
    /// False: the daily task fails on every fresh install.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_MissingOrEmptyTempFolder_DoesNothing(bool exists)
    {
        if (!exists)
        {
            Directory.Delete(_temp);
        }

        var progress = new TaskProgress();

        var thrown = await Record.ExceptionAsync(() => Task().ExecuteAsync(progress, CancellationToken.None));

        Assert.Null(thrown);
        Assert.Empty(progress.Values);
    }

    /// <summary>
    /// A missing progress reporter is refused.
    /// True: the mistake is named at the call.
    /// False: the task fails part way through, after deleting some files.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NullProgress_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Task().ExecuteAsync(null!, CancellationToken.None));
    }

    /// <summary>
    /// The task runs once a day by default.
    /// True: leftovers are cleared without the administrator scheduling anything.
    /// False: the task never runs on a fresh install.
    /// </summary>
    [Fact]
    public void GetDefaultTriggers_IsOneDailyInterval()
    {
        var trigger = Assert.Single(Task().GetDefaultTriggers());

        Assert.Equal(TaskTriggerInfoType.IntervalTrigger, trigger.Type);
        Assert.Equal(TimeSpan.FromDays(1).Ticks, trigger.IntervalTicks);
    }

    /// <summary>
    /// The task keeps the key Jellyfin stores its schedule under.
    /// True: an administrator's schedule survives an upgrade.
    /// False: a renamed key orphans the saved schedule and the task reverts to its default.
    /// </summary>
    [Fact]
    public void Key_IsTheStoredTaskKey()
    {
        Assert.Equal("ServerSyncCleanupTempFiles", Task().Key);
    }

    /// <summary>
    /// Each missing dependency is refused.
    /// True: a wiring mistake fails when the task is built.
    /// False: the task fails on its first scheduled run.
    /// </summary>
    [Fact]
    public void Constructor_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CleanupTempFilesTask(null!, _config));
        Assert.Throws<ArgumentNullException>(() => new CleanupTempFilesTask(NullLogger<CleanupTempFilesTask>.Instance, null!));
    }
}
