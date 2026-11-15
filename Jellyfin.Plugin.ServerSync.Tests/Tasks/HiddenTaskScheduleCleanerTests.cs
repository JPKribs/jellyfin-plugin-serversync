using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tasks;
using Jellyfin.Plugin.ServerSync.Tasks.ContentSync;
using Jellyfin.Plugin.ServerSync.Tasks.Information;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Tasks;

/// <summary>
/// The cleaner removes the saved schedule of this plugin's hidden tasks and of nothing else, once Jellyfin has registered them.
/// </summary>
public sealed class HiddenTaskScheduleCleanerTests
{
    private readonly List<IScheduledTaskWorker> _workers = new();
    private bool _startupCompleted;

    private sealed class ForeignHiddenTask : IScheduledTask, IConfigurableScheduledTask
    {
        public string Name => "Foreign";

        public string Key => "ForeignHidden";

        public string Description => "Another plugin's hidden task.";

        public string Category => "Other";

        public bool IsHidden => true;

        public bool IsEnabled => true;

        public bool IsLogged => true;

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) => Task.CompletedTask;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    }

    private sealed class Worker
    {
        public Worker(IScheduledTask task, int triggers)
        {
            IReadOnlyList<TaskTriggerInfo> current = new TaskTriggerInfo[triggers];
            var (proxy, stub) = InterfaceStub.Create<IScheduledTaskWorker>();
            stub.Answer("get_ScheduledTask", _ => task);
            stub.Answer("get_Triggers", _ => current);
            stub.Answer("set_Triggers", args =>
            {
                current = (IReadOnlyList<TaskTriggerInfo>)args[0]!;
                Writes++;
                return null;
            });
            Proxy = proxy;
            Triggers = () => current;
        }

        public IScheduledTaskWorker Proxy { get; }

        public Func<IReadOnlyList<TaskTriggerInfo>> Triggers { get; }

        public int Writes { get; private set; }
    }

    private static IScheduledTask HiddenPluginTask() => new SyncCollectionsTask(
        NullLogger<SyncCollectionsTask>.Instance,
        new FakeConfigManager(),
        new FakeClientFactory(),
        InterfaceStub.Create<ILibraryManager>().Proxy,
        InterfaceStub.Create<ICollectionManager>().Proxy,
        InterfaceStub.Create<ILocalItems>().Proxy);

    private static IScheduledTask VisiblePluginTask()
        => new SyncContentTask(new TaskServiceProvider(), new FakeConfigManager(), NullLogger<SyncContentTask>.Instance);

    private Worker Add(IScheduledTask task, int triggers)
    {
        var worker = new Worker(task, triggers);
        _workers.Add(worker.Proxy);
        return worker;
    }

    private HiddenTaskScheduleCleaner Cleaner()
    {
        var (taskManager, taskStub) = InterfaceStub.Create<ITaskManager>();
        taskStub.Answer("get_ScheduledTasks", _ => _workers);
        var (host, hostStub) = InterfaceStub.Create<IServerApplicationHost>();
        hostStub.Answer("get_CoreStartupHasCompleted", _ => _startupCompleted);
        return new HiddenTaskScheduleCleaner(taskManager, host, NullLogger<HiddenTaskScheduleCleaner>.Instance);
    }

    /// <summary>
    /// A hidden task of this plugin that still has a schedule loses it, and is counted.
    /// True: a per sync type task no longer runs on its own beside Sync Content and Sync Information.
    /// False: a stale timer runs each sync type twice as often as the administrator set.
    /// </summary>
    [Fact]
    public void ClearHiddenSchedules_HiddenPluginTaskWithTriggers_ClearsThem()
    {
        var worker = Add(HiddenPluginTask(), triggers: 2);
        using var cleaner = Cleaner();

        var cleared = cleaner.ClearHiddenSchedules();

        Assert.Equal(1, cleared);
        Assert.Empty(worker.Triggers());
    }

    /// <summary>
    /// A hidden task of this plugin with no schedule is not written and not counted.
    /// True: a server already cleaned is left alone on every later start.
    /// False: Jellyfin rewrites each task file on every start for nothing.
    /// </summary>
    [Fact]
    public void ClearHiddenSchedules_HiddenPluginTaskWithoutTriggers_IsNotWritten()
    {
        var worker = Add(HiddenPluginTask(), triggers: 0);
        using var cleaner = Cleaner();

        var cleared = cleaner.ClearHiddenSchedules();

        Assert.Equal(0, cleared);
        Assert.Equal(0, worker.Writes);
    }

    /// <summary>
    /// A visible task of this plugin keeps its schedule.
    /// True: Sync Content and Sync Information keep running on the schedule the administrator set.
    /// False: the plugin's own scheduled tasks stop running after a restart.
    /// </summary>
    [Fact]
    public void ClearHiddenSchedules_VisiblePluginTask_KeepsItsTriggers()
    {
        var worker = Add(VisiblePluginTask(), triggers: 1);
        using var cleaner = Cleaner();

        var cleared = cleaner.ClearHiddenSchedules();

        Assert.Equal(0, cleared);
        Assert.Single(worker.Triggers());
    }

    /// <summary>
    /// A hidden task from another assembly keeps its schedule.
    /// True: the cleaner never touches a task it does not own.
    /// False: another plugin's hidden task silently stops running.
    /// </summary>
    [Fact]
    public void ClearHiddenSchedules_HiddenTaskOfAnotherAssembly_KeepsItsTriggers()
    {
        var worker = Add(new ForeignHiddenTask(), triggers: 1);
        using var cleaner = Cleaner();

        var cleared = cleaner.ClearHiddenSchedules();

        Assert.Equal(0, cleared);
        Assert.Single(worker.Triggers());
    }

    /// <summary>
    /// With no tasks registered nothing is cleared.
    /// True: an empty task list is a quiet no-op.
    /// False: the cleaner would throw during startup.
    /// </summary>
    [Fact]
    public void ClearHiddenSchedules_NoTasks_ReturnsZero()
    {
        using var cleaner = Cleaner();

        Assert.Equal(0, cleaner.ClearHiddenSchedules());
    }

    /// <summary>
    /// Once startup has finished, starting the service clears the hidden schedules in the background.
    /// True: the stale timers are gone shortly after every start.
    /// False: the cleaner is registered but never does its work.
    /// </summary>
    [Fact]
    public async Task StartAsync_StartupCompleted_ClearsTheHiddenSchedules()
    {
        var worker = Add(HiddenPluginTask(), triggers: 1);
        _startupCompleted = true;
        using var cleaner = Cleaner();

        await cleaner.StartAsync(CancellationToken.None);
        await cleaner.StopAsync(CancellationToken.None);

        Assert.Empty(worker.Triggers());
    }

    /// <summary>
    /// The plugin's tasks being registered is enough to start, even before Jellyfin reports startup as finished.
    /// True: the schedules are cleared as soon as they can be read.
    /// False: the cleaner waits out a flag that some hosts never set.
    /// </summary>
    [Fact]
    public async Task StartAsync_TasksRegisteredBeforeStartupCompletes_ClearsTheHiddenSchedules()
    {
        var worker = Add(HiddenPluginTask(), triggers: 1);
        _startupCompleted = false;
        using var cleaner = Cleaner();

        await cleaner.StartAsync(CancellationToken.None);
        await cleaner.StopAsync(CancellationToken.None);

        Assert.Empty(worker.Triggers());
    }

    /// <summary>
    /// Stopping while the cleaner still waits for startup ends the wait without an error and clears nothing.
    /// True: a server that shuts down during startup stops cleanly.
    /// False: shutdown hangs for the ten minute limit or surfaces a cancellation as a fault.
    /// </summary>
    [Fact]
    public async Task StopAsync_WhileWaitingForStartup_EndsTheWait()
    {
        var foreign = Add(new ForeignHiddenTask(), triggers: 1);
        _startupCompleted = false;
        using var cleaner = Cleaner();
        await cleaner.StartAsync(CancellationToken.None);

        await cleaner.StopAsync(CancellationToken.None);

        Assert.Equal(0, foreign.Writes);
    }

    /// <summary>
    /// Stopping a cleaner that never started does nothing.
    /// True: a host that stops services it did not start gets no error.
    /// False: shutdown throws a null reference.
    /// </summary>
    [Fact]
    public async Task StopAsync_NeverStarted_DoesNothing()
    {
        using var cleaner = Cleaner();

        var thrown = await Record.ExceptionAsync(() => cleaner.StopAsync(CancellationToken.None));

        Assert.Null(thrown);
    }

    /// <summary>
    /// Each missing dependency is refused by name.
    /// True: a wiring mistake fails when the service is built.
    /// False: it would fail later on a background thread where nothing reports it.
    /// </summary>
    [Theory]
    [InlineData("taskManager")]
    [InlineData("applicationHost")]
    [InlineData("logger")]
    public void Constructor_NullArgument_Throws(string missing)
    {
        var taskManager = missing == "taskManager" ? null : InterfaceStub.Create<ITaskManager>().Proxy;
        var host = missing == "applicationHost" ? null : InterfaceStub.Create<IServerApplicationHost>().Proxy;
        var logger = missing == "logger" ? null : NullLogger<HiddenTaskScheduleCleaner>.Instance;

        var thrown = Assert.Throws<ArgumentNullException>(() => new HiddenTaskScheduleCleaner(taskManager!, host!, logger!));

        Assert.Equal(missing, thrown.ParamName);
    }
}
