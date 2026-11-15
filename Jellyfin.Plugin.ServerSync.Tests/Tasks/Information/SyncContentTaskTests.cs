using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Tasks.ContentSync;
using Jellyfin.Plugin.ServerSync.Tasks.Information;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Tasks.Information;

/// <summary>
/// Sync Content runs its steps under one progress bar, never downloads after a failed scan, still runs the later steps, and ends as failed when any step failed.
/// </summary>
public sealed class SyncContentTaskTests
{
    private readonly FakeConfigManager _config = new();
    private readonly TaskServiceProvider _services = new();

    private SyncContentTask Task() => new(_services, _config, NullLogger<SyncContentTask>.Instance);

    /// <summary>
    /// With Content off the task reports done and builds no step.
    /// True: a server that does not sync content finishes the scheduled task at once.
    /// False: a scan and download run for a sync type that is off.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ContentSyncOff_ReportsDoneAndBuildsNothing()
    {
        _config.Configuration.EnableContentSync = false;
        var progress = new TaskProgress();

        await Task().ExecuteAsync(progress, CancellationToken.None);

        Assert.Equal(new[] { 100.0 }, progress.Values);
        Assert.Empty(_services.Requested);
    }

    /// <summary>
    /// When the scan cannot run, the download is skipped, collections are still tried, and the task fails naming both failures.
    /// True: a stale queue is never downloaded, and the task shows as failed in the dashboard.
    /// False: files are downloaded from a table the scan did not refresh, or a failed run reads as a success.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ScanFails_SkipsTheDownloadAndFailsTheTask()
    {
        _config.Configuration.EnableContentSync = true;

        var thrown = await Assert.ThrowsAsync<AggregateException>(() => Task().ExecuteAsync(new TaskProgress(), CancellationToken.None));

        Assert.Equal(2, thrown.InnerExceptions.Count);
        Assert.Contains(nameof(UpdateSyncTablesTask), thrown.InnerExceptions[0].Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SyncCollectionsTask), thrown.InnerExceptions[1].Message, StringComparison.Ordinal);
        Assert.DoesNotContain(thrown.InnerExceptions, e => e.Message.Contains(nameof(SyncMissingContentTask), StringComparison.Ordinal));
    }

    /// <summary>
    /// A failed step moves the bar to the end of its share, and the run still ends at 100.
    /// True: the bar keeps moving past a failed step, with the scan ending at 25 and collections at 100.
    /// False: the bar freezes at the failed step for the rest of the run.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_StepsFail_ProgressStillReachesTheEnd()
    {
        _config.Configuration.EnableContentSync = true;
        var progress = new TaskProgress();

        await Assert.ThrowsAsync<AggregateException>(() => Task().ExecuteAsync(progress, CancellationToken.None));

        Assert.Equal(new[] { 25.0, 100.0, 100.0 }, progress.Values);
    }

    /// <summary>
    /// A cancelled token stops the task before any step is built.
    /// True: stopping the task ends it as cancelled.
    /// False: a stopped task builds and starts a scan anyway, or is recorded as failed.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CancelledToken_ThrowsBeforeAnyStep()
    {
        _config.Configuration.EnableContentSync = true;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task().ExecuteAsync(new TaskProgress(), cts.Token));

        Assert.Empty(_services.Requested);
    }

    /// <summary>
    /// A missing progress reporter is refused.
    /// True: the mistake is named at the call.
    /// False: the task fails inside its first step.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NullProgress_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Task().ExecuteAsync(null!, CancellationToken.None));
    }

    /// <summary>
    /// The task is visible, enabled, logged, and keeps its stored key.
    /// True: Sync Content appears in the scheduled task list under the key its schedule was saved with.
    /// False: the one task an administrator schedules is hidden, or loses its schedule on upgrade.
    /// </summary>
    [Fact]
    public void Properties_ShowTheTaskUnderItsStoredKey()
    {
        var task = Task();

        Assert.False(task.IsHidden);
        Assert.True(task.IsEnabled);
        Assert.True(task.IsLogged);
        Assert.Equal("ServerSyncContent", task.Key);
        Assert.Equal("Sync Content", task.Name);
    }

    /// <summary>
    /// The task runs every six hours by default.
    /// True: content follows its sources without the administrator scheduling anything.
    /// False: a fresh install never scans.
    /// </summary>
    [Fact]
    public void GetDefaultTriggers_IsOneSixHourInterval()
    {
        var trigger = Assert.Single(Task().GetDefaultTriggers());

        Assert.Equal(TaskTriggerInfoType.IntervalTrigger, trigger.Type);
        Assert.Equal(TimeSpan.FromHours(6).Ticks, trigger.IntervalTicks);
    }

    /// <summary>
    /// Each missing dependency is refused by name.
    /// True: a wiring mistake fails when the task is built.
    /// False: the task fails on its first scheduled run.
    /// </summary>
    [Theory]
    [InlineData("services")]
    [InlineData("configManager")]
    [InlineData("logger")]
    public void Constructor_NullArgument_Throws(string missing)
    {
        var thrown = Assert.Throws<ArgumentNullException>(() => new SyncContentTask(
            missing == "services" ? null! : _services,
            missing == "configManager" ? null! : _config,
            missing == "logger" ? null! : NullLogger<SyncContentTask>.Instance));

        Assert.Equal(missing, thrown.ParamName);
    }
}
