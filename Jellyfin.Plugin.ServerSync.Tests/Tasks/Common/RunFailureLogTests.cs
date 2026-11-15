using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Tasks.Common;

/// <summary>
/// The run failure log keeps one failure for each sync type and phase, remembers when it first stood, and raises it to the administrator once.
/// </summary>
public sealed class RunFailureLogTests
{
    private static readonly DateTime Old = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static void Record(IPluginConfigurationManager? config, string module, string phase, string reason)
        => TaskInternals.Call("RunFailureLog", "Record", config, module, phase, reason, NullLogger.Instance, "Test Task");

    private static void Clear(IPluginConfigurationManager? config, string module, string phase)
        => TaskInternals.Call("RunFailureLog", "Clear", config, module, phase, NullLogger.Instance, "Test Task");

    private static IReadOnlyList<SyncRunFailure> TakeUnreported(IPluginConfigurationManager? config, TimeSpan standing)
        => (IReadOnlyList<SyncRunFailure>)TaskInternals.Call("RunFailureLog", "TakeUnreported", config, standing)!;

    private static IPluginConfigurationManager FailingSaves(PluginConfiguration configuration)
    {
        var (proxy, stub) = InterfaceStub.Create<IPluginConfigurationManager>();
        stub.Answer("get_Configuration", _ => configuration);
        stub.Answer(nameof(IPluginConfigurationManager.SaveConfiguration), _ => throw new InvalidOperationException("disk full"));
        return proxy;
    }

    /// <summary>
    /// A first failure is stored with its sync type, phase, and reason, and the configuration is saved.
    /// True: the dashboard can show why the last run failed after a restart.
    /// False: a failed run leaves no trace and the status reads as healthy.
    /// </summary>
    [Fact]
    public void Record_FirstFailure_StoresItAndSaves()
    {
        var config = new FakeConfigManager();

        Record(config, "History", "Sync", "no scan server is reachable");

        var failure = Assert.Single(config.Configuration.LastRunFailures);
        Assert.Equal("History", failure.ModuleKey);
        Assert.Equal("Sync", failure.Phase);
        Assert.Equal("no scan server is reachable", failure.Reason);
        Assert.False(failure.Reported);
        Assert.Equal(failure.Timestamp, failure.FirstSeen);
        Assert.Equal(1, config.Saves);
    }

    /// <summary>
    /// A repeat failure replaces the stored one, matched without regard to case, and keeps when it first stood and whether it was raised.
    /// True: a failure that persists is raised once, measured from its first run.
    /// False: every failing run restarts the clock, so the administrator is never told, or is told every run.
    /// </summary>
    [Fact]
    public void Record_RepeatFailure_ReplacesItAndKeepsFirstSeenAndReported()
    {
        var config = new FakeConfigManager();
        config.Configuration.LastRunFailures.Add(new SyncRunFailure
        {
            ModuleKey = "History",
            Phase = "Sync",
            Reason = "old reason",
            Timestamp = Old.AddDays(1),
            FirstSeen = Old,
            Reported = true
        });

        Record(config, "history", "SYNC", "new reason");

        var failure = Assert.Single(config.Configuration.LastRunFailures);
        Assert.Equal("new reason", failure.Reason);
        Assert.Equal(Old, failure.FirstSeen);
        Assert.True(failure.Reported);
        Assert.True(failure.Timestamp > Old.AddDays(1));
    }

    /// <summary>
    /// A stored failure with no first seen time takes its own timestamp as the first seen time.
    /// True: a failure saved by an older configuration keeps its real age.
    /// False: the failure would look brand new on every run and never be raised.
    /// </summary>
    [Fact]
    public void Record_PriorFailureWithoutFirstSeen_UsesItsTimestamp()
    {
        var config = new FakeConfigManager();
        config.Configuration.LastRunFailures.Add(new SyncRunFailure { ModuleKey = "User", Phase = "Refresh", Timestamp = Old });

        Record(config, "User", "Refresh", "again");

        Assert.Equal(Old, Assert.Single(config.Configuration.LastRunFailures).FirstSeen);
    }

    /// <summary>
    /// A Refresh failure and a Sync failure of one sync type are kept side by side, as are failures of other sync types.
    /// True: fixing the scan does not hide a failing apply.
    /// False: the later phase overwrites the earlier one and half the problem disappears.
    /// </summary>
    [Fact]
    public void Record_OtherPhaseOrSyncType_IsKeptBeside()
    {
        var config = new FakeConfigManager();

        Record(config, "History", "Refresh", "scan failed");
        Record(config, "History", "Sync", "apply failed");
        Record(config, "User", "Sync", "apply failed");

        Assert.Equal(
            new[] { "History/Refresh", "History/Sync", "User/Sync" },
            config.Configuration.LastRunFailures.Select(f => f.ModuleKey + "/" + f.Phase).ToArray());
    }

    /// <summary>
    /// A save that throws is swallowed.
    /// True: the run still reports its real outcome when the configuration cannot be written.
    /// False: a full disk would replace the run's own error with a save error.
    /// </summary>
    [Fact]
    public void Record_SaveThrows_DoesNotThrow()
    {
        var configuration = new PluginConfiguration();

        var thrown = Xunit.Record.Exception(() => Record(FailingSaves(configuration), "History", "Sync", "reason"));

        Assert.Null(thrown);
        Assert.Single(configuration.LastRunFailures);
    }

    /// <summary>
    /// A missing configuration manager is refused.
    /// True: a wiring mistake fails at once.
    /// False: the swallow-all catch would hide it and no failure would ever be recorded.
    /// </summary>
    [Fact]
    public void Record_NullConfigManager_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Record(null, "History", "Sync", "reason"));
    }

    /// <summary>
    /// Clearing removes only the named sync type and phase, matched without regard to case, and saves.
    /// True: a clean apply clears its own failure and leaves the scan's standing.
    /// False: one clean phase wipes every failure, or the failure stays after the problem is fixed.
    /// </summary>
    [Fact]
    public void Clear_MatchingFailure_RemovesOnlyItAndSaves()
    {
        var config = new FakeConfigManager();
        config.Configuration.LastRunFailures.Add(new SyncRunFailure { ModuleKey = "History", Phase = "Sync" });
        config.Configuration.LastRunFailures.Add(new SyncRunFailure { ModuleKey = "History", Phase = "Refresh" });

        Clear(config, "HISTORY", "sync");

        Assert.Equal("Refresh", Assert.Single(config.Configuration.LastRunFailures).Phase);
        Assert.Equal(1, config.Saves);
    }

    /// <summary>
    /// Clearing when nothing is stored does not save.
    /// True: a healthy run writes the configuration file no more than it must.
    /// False: every clean run of every sync type rewrites the configuration.
    /// </summary>
    [Fact]
    public void Clear_NothingStored_DoesNotSave()
    {
        var config = new FakeConfigManager();

        Clear(config, "History", "Sync");

        Assert.Equal(0, config.Saves);
    }

    /// <summary>
    /// A save that throws while clearing is swallowed.
    /// True: a clean run stays clean when the configuration cannot be written.
    /// False: a successful run would end as failed because of a save error.
    /// </summary>
    [Fact]
    public void Clear_SaveThrows_DoesNotThrow()
    {
        var configuration = new PluginConfiguration();
        configuration.LastRunFailures.Add(new SyncRunFailure { ModuleKey = "History", Phase = "Sync" });

        var thrown = Xunit.Record.Exception(() => Clear(FailingSaves(configuration), "History", "Sync"));

        Assert.Null(thrown);
    }

    /// <summary>
    /// Failures that have stood past the limit and were not raised are returned, marked as raised, and saved once.
    /// True: the administrator is told about a standing failure exactly once.
    /// False: the same failure is raised on every check, or never.
    /// </summary>
    [Fact]
    public void TakeUnreported_StandingFailures_ReturnsAndMarksThem()
    {
        var config = new FakeConfigManager();
        var standing = new SyncRunFailure { ModuleKey = "History", Phase = "Sync", Timestamp = DateTime.UtcNow, FirstSeen = Old };
        var legacy = new SyncRunFailure { ModuleKey = "User", Phase = "Sync", Timestamp = Old };
        config.Configuration.LastRunFailures.Add(standing);
        config.Configuration.LastRunFailures.Add(legacy);

        var first = TakeUnreported(config, TimeSpan.FromHours(1));
        var second = TakeUnreported(config, TimeSpan.FromHours(1));

        Assert.Equal(new[] { standing, legacy }, first);
        Assert.True(standing.Reported);
        Assert.True(legacy.Reported);
        Assert.Empty(second);
        Assert.Equal(1, config.Saves);
    }

    /// <summary>
    /// A failure younger than the limit, or one already raised, is left alone and nothing is saved.
    /// True: a single bad run does not alert, and an alert is not repeated.
    /// False: every transient failure reaches the administrator at once.
    /// </summary>
    [Fact]
    public void TakeUnreported_RecentOrAlreadyReported_ReturnsNothing()
    {
        var config = new FakeConfigManager();
        var recent = new SyncRunFailure { ModuleKey = "History", Phase = "Sync", Timestamp = DateTime.UtcNow, FirstSeen = DateTime.UtcNow };
        var told = new SyncRunFailure { ModuleKey = "User", Phase = "Sync", Timestamp = Old, FirstSeen = Old, Reported = true };
        config.Configuration.LastRunFailures.Add(recent);
        config.Configuration.LastRunFailures.Add(told);

        var due = TakeUnreported(config, TimeSpan.FromHours(1));

        Assert.Empty(due);
        Assert.False(recent.Reported);
        Assert.Equal(0, config.Saves);
    }

    /// <summary>
    /// An empty log returns nothing.
    /// True: a healthy server raises no failure.
    /// False: the check would throw or save on every pass.
    /// </summary>
    [Fact]
    public void TakeUnreported_NoFailures_ReturnsNothing()
    {
        var config = new FakeConfigManager();

        var due = TakeUnreported(config, TimeSpan.Zero);

        Assert.Empty(due);
        Assert.Equal(0, config.Saves);
    }
}
