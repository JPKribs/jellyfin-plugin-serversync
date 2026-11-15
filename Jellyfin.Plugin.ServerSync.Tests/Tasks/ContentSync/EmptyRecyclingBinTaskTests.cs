using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Tasks.ContentSync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Tasks.ContentSync;

/// <summary>
/// The Recycling Bin task deletes recycled files past their retention period, and refuses to run when the bin is off, unset, or overlaps a library.
/// </summary>
public sealed class EmptyRecyclingBinTaskTests : IDisposable
{
    private readonly string _root;
    private readonly string _bin;
    private readonly FakeConfigManager _config = new();

    public EmptyRecyclingBinTaskTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "serversync-bin-" + Guid.NewGuid().ToString("N"));
        _bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(_bin);
        _config.Configuration.EnableContentSync = true;
        _config.Configuration.EnableRecyclingBin = true;
        _config.Configuration.RecyclingBinPath = _bin;
        _config.Configuration.RecyclingBinRetentionDays = 7;
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

    private EmptyRecyclingBinTask Task() => new(NullLogger<EmptyRecyclingBinTask>.Instance, _config);

    private string Recycled(string name, int daysAgo)
    {
        var path = Path.Combine(_bin, FileOperationUtilities.GenerateRecycledFileName("/media/" + name, DateTime.UtcNow.AddDays(-daysAgo)));
        File.WriteAllText(path, "recycled");
        return path;
    }

    /// <summary>
    /// A recycled file older than the retention period is deleted, and a younger one is kept.
    /// True: the bin gives back space after Keep For days and no sooner.
    /// False: the bin grows without end, or a file is lost before the administrator can restore it.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ExpiredAndFreshFiles_DeletesOnlyTheExpired()
    {
        var expired = Recycled("old.mkv", daysAgo: 30);
        var fresh = Recycled("new.mkv", daysAgo: 1);

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(fresh));
    }

    /// <summary>
    /// A file the Recycling Bin did not name is never deleted.
    /// True: a file someone else put in the folder is safe however old it is.
    /// False: pointing the bin at a shared folder deletes other people's files.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_FileNotNamedByTheBin_IsKept()
    {
        var stranger = Path.Combine(_bin, "notes.txt");
        File.WriteAllText(stranger, "mine");
        File.SetLastWriteTimeUtc(stranger, DateTime.UtcNow.AddDays(-365));

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.True(File.Exists(stranger));
    }

    /// <summary>
    /// A run that cleans reports 0 at the start and 100 at the end.
    /// True: the task's bar starts and finishes.
    /// False: the task looks stuck at its last value.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Runs_ReportsStartAndEnd()
    {
        var progress = new TaskProgress();

        await Task().ExecuteAsync(progress, CancellationToken.None);

        Assert.Equal(new[] { 0.0, 100.0 }, progress.Values);
    }

    /// <summary>
    /// Content being off, the bin being off, or the path being unset each skip the run and delete nothing.
    /// True: the task acts only when the administrator turned the bin on and named its folder.
    /// False: files are deleted from a bin the settings say is not in use.
    /// </summary>
    [Theory]
    [InlineData("content")]
    [InlineData("bin")]
    [InlineData("path")]
    public async Task ExecuteAsync_SwitchedOffOrUnset_DeletesNothing(string off)
    {
        var expired = Recycled("old.mkv", daysAgo: 30);
        _config.Configuration.EnableContentSync = off != "content";
        _config.Configuration.EnableRecyclingBin = off != "bin";
        _config.Configuration.RecyclingBinPath = off == "path" ? string.Empty : _bin;
        var progress = new TaskProgress();

        await Task().ExecuteAsync(progress, CancellationToken.None);

        Assert.True(File.Exists(expired));
        Assert.Empty(progress.Values);
    }

    /// <summary>
    /// A bin that overlaps a library root is never cleaned.
    /// True: a bin placed inside a library cannot delete media.
    /// False: media whose name looks recycled is permanently deleted.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_BinOverlapsLibraryRoot_DeletesNothing()
    {
        var expired = Recycled("old.mkv", daysAgo: 30);
        var server = new SourceServer { Key = "a", Url = "http://a", ApiKey = "k" };
        server.LibraryMappings.Add(new LibraryMapping { SourceLibraryId = "lib", LocalRootPath = _root });
        _config.Configuration.Servers.Add(server);

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.True(File.Exists(expired));
    }

    /// <summary>
    /// A bin folder that does not exist is a quiet no-op.
    /// True: a bin that was never used does not fail the daily task.
    /// False: the task errors every day until something is recycled.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_MissingBinFolder_DoesNotThrow()
    {
        _config.Configuration.RecyclingBinPath = Path.Combine(_root, "absent");

        var thrown = await Record.ExceptionAsync(() => Task().ExecuteAsync(new TaskProgress(), CancellationToken.None));

        Assert.Null(thrown);
    }

    /// <summary>
    /// A missing progress reporter is refused.
    /// True: the mistake is named at the call.
    /// False: the task fails after it has started deleting.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NullProgress_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Task().ExecuteAsync(null!, CancellationToken.None));
    }

    /// <summary>
    /// The task runs once a day by default and keeps its stored key.
    /// True: the bin empties itself without the administrator scheduling anything, and a saved schedule survives an upgrade.
    /// False: the task never runs, or a renamed key orphans its schedule.
    /// </summary>
    [Fact]
    public void GetDefaultTriggers_IsOneDailyIntervalUnderTheStoredKey()
    {
        var task = Task();

        var trigger = Assert.Single(task.GetDefaultTriggers());

        Assert.Equal(TaskTriggerInfoType.IntervalTrigger, trigger.Type);
        Assert.Equal(TimeSpan.FromDays(1).Ticks, trigger.IntervalTicks);
        Assert.Equal("ServerSyncEmptyRecyclingBin", task.Key);
    }

    /// <summary>
    /// Each missing dependency is refused.
    /// True: a wiring mistake fails when the task is built.
    /// False: the task fails on its first scheduled run.
    /// </summary>
    [Fact]
    public void Constructor_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new EmptyRecyclingBinTask(null!, _config));
        Assert.Throws<ArgumentNullException>(() => new EmptyRecyclingBinTask(NullLogger<EmptyRecyclingBinTask>.Instance, null!));
    }
}
