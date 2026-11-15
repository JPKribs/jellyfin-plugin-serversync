using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.UserSync;
using Jellyfin.Plugin.ServerSync.Tasks.HistorySync;
using Jellyfin.Plugin.ServerSync.Tasks.Information;
using Jellyfin.Plugin.ServerSync.Tasks.PlaylistSync;
using Jellyfin.Plugin.ServerSync.Tasks.UserSync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Tasks.Information;

/// <summary>
/// Sync Information runs a scan then an apply for each sync type that is on, skips the apply after a failed scan, goes on to the next sync type, and ends as failed when any step failed.
/// </summary>
public sealed class SyncInformationTaskTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly FakeConfigManager _config = new();
    private readonly TaskServiceProvider _services = new();

    public SyncInformationTaskTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-information-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        var config = _config.Configuration;
        config.EnableMetadataSync = false;
        config.EnablePeopleSync = false;
        config.EnableUserSync = false;
        config.EnableHistorySync = false;
        config.EnablePlaylistSync = false;
    }

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private SyncInformationTask Task() => new(_services, _config, NullLogger<SyncInformationTask>.Instance);

    private void RegisterUserTaskServices()
    {
        _services.SuppliesLoggers = true;
        _services
            .With<IPluginConfigurationManager>(_config)
            .With<ISourceServerClientFactory>(new FakeClientFactory())
            .With(InterfaceStub.Create<IUserManager>().Proxy)
            .With(InterfaceStub.Create<IProviderManager>().Proxy)
            .With(InterfaceStub.Create<IServerConfigurationManager>().Proxy)
            .With(new UserSyncTableService(NullLogger<UserSyncTableService>.Instance))
            .With(new UserSyncTableManager(new FixedProvider(_database), NullLogger<UserSyncTableManager>.Instance));
    }

    /// <summary>
    /// With every sync type off the task reports done and builds no step.
    /// True: a server that syncs only content finishes this task at once.
    /// False: scans run for sync types that are off.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NoSyncTypeOn_ReportsDoneAndBuildsNothing()
    {
        var progress = new TaskProgress();

        await Task().ExecuteAsync(progress, CancellationToken.None);

        Assert.Equal(new[] { 100.0 }, progress.Values);
        Assert.Empty(_services.Requested);
    }

    /// <summary>
    /// A sync type that is on has its scan built first and its apply second, and a clean run ends at 100 without an error.
    /// True: rows are applied from a table the same run just refreshed.
    /// False: the apply runs on stale rows, or a clean run is reported as failed.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UsersOn_BuildsTheScanThenTheApply()
    {
        _config.Configuration.EnableUserSync = true;
        RegisterUserTaskServices();
        var progress = new TaskProgress();

        await Task().ExecuteAsync(progress, CancellationToken.None);

        var loggers = _services.Requested.Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ILogger<>)).ToArray();
        Assert.Equal(new[] { typeof(ILogger<RefreshUserSyncTableTask>), typeof(ILogger<SyncMissingUserTask>) }, loggers);
        Assert.Equal(100.0, progress.Values[^1]);
    }

    /// <summary>
    /// Only the sync types that are on are built.
    /// True: turning a sync type off stops its scheduled comparison.
    /// False: a sync type the administrator turned off still scans its sources.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_OnlyUsersOn_BuildsNoOtherSyncType()
    {
        _config.Configuration.EnableUserSync = true;
        RegisterUserTaskServices();

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.DoesNotContain(typeof(ILogger<RefreshHistorySyncTableTask>), _services.Requested);
        Assert.DoesNotContain(typeof(ILogger<SyncPlaylistsTask>), _services.Requested);
    }

    /// <summary>
    /// When a scan cannot run, its apply is skipped, the next sync type still runs, and the task fails with one failure for each failed step.
    /// True: a stale table is never applied, one broken sync type does not stop the others, and the task shows as failed.
    /// False: rows from a failed scan are applied, or the first failure ends the whole run.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ScansFail_SkipsEachApplyAndFailsTheTask()
    {
        _config.Configuration.EnableUserSync = true;
        _config.Configuration.EnableHistorySync = true;

        var thrown = await Assert.ThrowsAsync<AggregateException>(() => Task().ExecuteAsync(new TaskProgress(), CancellationToken.None));

        Assert.Equal(2, thrown.InnerExceptions.Count);
        Assert.Contains(nameof(RefreshUserSyncTableTask), thrown.InnerExceptions[0].Message, StringComparison.Ordinal);
        Assert.Contains(nameof(RefreshHistorySyncTableTask), thrown.InnerExceptions[1].Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bar is split evenly across the steps, with two for a scan and apply pair and one for Playlists, and a failed step ends its own share.
    /// True: with Users and Playlists on, the failed Users scan ends at a third and Playlists ends at 100.
    /// False: the bar stops at the failed step, or Playlists is given a share for an apply it does not have.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_StepsFail_ProgressFollowsTheEvenShares()
    {
        _config.Configuration.EnableUserSync = true;
        _config.Configuration.EnablePlaylistSync = true;
        var progress = new TaskProgress();

        await Assert.ThrowsAsync<AggregateException>(() => Task().ExecuteAsync(progress, CancellationToken.None));

        Assert.Equal(3, progress.Values.Count);
        Assert.Equal(100.0 / 3, progress.Values[0], 6);
        Assert.Equal(100.0, progress.Values[1], 6);
        Assert.Equal(100.0, progress.Values[2]);
    }

    /// <summary>
    /// A cancelled token stops the task before any step is built.
    /// True: stopping the task ends it as cancelled.
    /// False: a stopped task starts a scan anyway, or is recorded as failed.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CancelledToken_ThrowsBeforeAnyStep()
    {
        _config.Configuration.EnableUserSync = true;
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
    /// True: Sync Information appears in the scheduled task list under the key its schedule was saved with.
    /// False: the task is hidden, or loses its schedule on upgrade.
    /// </summary>
    [Fact]
    public void Properties_ShowTheTaskUnderItsStoredKey()
    {
        var task = Task();

        Assert.False(task.IsHidden);
        Assert.True(task.IsEnabled);
        Assert.True(task.IsLogged);
        Assert.Equal("ServerSyncInformation", task.Key);
        Assert.Equal("Sync Information", task.Name);
    }

    /// <summary>
    /// The task runs every six hours by default.
    /// True: anything the hints missed is picked up without the administrator scheduling anything.
    /// False: a fresh install never runs the full comparison.
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
        var thrown = Assert.Throws<ArgumentNullException>(() => new SyncInformationTask(
            missing == "services" ? null! : _services,
            missing == "configManager" ? null! : _config,
            missing == "logger" ? null! : NullLogger<SyncInformationTask>.Instance));

        Assert.Equal(missing, thrown.ParamName);
    }
}
