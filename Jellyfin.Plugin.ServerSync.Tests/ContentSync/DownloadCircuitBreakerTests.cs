using System;
using Jellyfin.Plugin.ServerSync.Tasks;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.ContentSync;

/// <summary>
/// The download circuit breaker of Sync Content. Connecting to a server at the start of a run is not
/// a download, so only a finished download may clear the breaker's failure count.
/// </summary>
public class DownloadCircuitBreakerTests
{
    /// <summary>Reaches the task's admission step, which is protected.</summary>
    private abstract class Probe : SyncMissingContentTask
    {
        private Probe()
            : base(null!, null!, null!, null!, null!, null!, null!)
        {
        }

        public static bool Admit(CircuitBreaker breaker, out string? reason) => AdmitServer(breaker, out reason);

        public static void Settle(CircuitBreaker breaker, bool downloadsAttempted) => SettleAfterRun(breaker, downloadsAttempted);
    }

    private static CircuitBreaker Breaker(TimeSpan cooldown) => new(NullLogger.Instance, "SourceServer", failureThreshold: 5, cooldownPeriod: cooldown);

    /// <summary>
    /// Admitting a server to a run keeps the failures recorded before it.
    /// True: four failed downloads in one run and one in the next open the breaker.
    /// False: every run starts from zero, so a server failing a few downloads per run never trips it.
    /// </summary>
    [Fact]
    public void Admit_KeepsEarlierFailures()
    {
        var breaker = Breaker(TimeSpan.FromMinutes(5));
        for (var i = 0; i < 4; i++)
        {
            breaker.RecordFailure("download failed");
        }

        Assert.True(Probe.Admit(breaker, out var reason));
        Assert.Null(reason);
        Assert.Equal(4, breaker.ConsecutiveFailures);

        breaker.RecordFailure("download failed");

        Assert.True(breaker.IsOpen);
        Assert.False(Probe.Admit(breaker, out reason));
        Assert.Contains("Circuit breaker open", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Once the cooldown passes the server is admitted again, but on probation: the first failed
    /// download opens the breaker again, and only a successful one closes it.
    /// True: a server that is still broken costs one download per cooldown.
    /// False: the next run's connect closes the breaker and five more downloads fail first.
    /// </summary>
    [Fact]
    public void Admit_AfterCooldown_OneFailureReopens_AndOnlyASuccessCloses()
    {
        var cooldown = TimeSpan.FromMilliseconds(300);
        var breaker = Breaker(cooldown);
        for (var i = 0; i < 5; i++)
        {
            breaker.RecordFailure("download failed");
        }

        System.Threading.Thread.Sleep(cooldown + TimeSpan.FromMilliseconds(100));
        Assert.True(Probe.Admit(breaker, out _));
        Assert.Equal(5, breaker.ConsecutiveFailures);

        breaker.RecordFailure("still failing");
        Assert.True(breaker.IsOpen);

        System.Threading.Thread.Sleep(cooldown + TimeSpan.FromMilliseconds(100));
        Assert.True(Probe.Admit(breaker, out _));
        breaker.RecordSuccess();

        Assert.Equal(0, breaker.ConsecutiveFailures);
        breaker.RecordFailure("one more");
        Assert.False(breaker.IsOpen);
    }

    /// <summary>
    /// A server that connected and then had nothing to download forgets its earlier connection
    /// failures. One that downloaded keeps what its downloads recorded.
    /// True: a connect that failed now and then over weeks never opens the breaker on its own.
    /// False: occasional connect failures add up until a healthy server is paused.
    /// </summary>
    [Fact]
    public void SettleAfterRun_ForgetsConnectFailures_OnlyWhenNothingWasDownloaded()
    {
        var idle = Breaker(TimeSpan.FromMinutes(5));
        var busy = Breaker(TimeSpan.FromMinutes(5));
        for (var i = 0; i < 3; i++)
        {
            idle.RecordFailure("connection test failed");
            busy.RecordFailure("download failed");
        }

        Probe.Settle(idle, downloadsAttempted: false);
        Probe.Settle(busy, downloadsAttempted: true);

        Assert.Equal(0, idle.ConsecutiveFailures);
        Assert.Equal(3, busy.ConsecutiveFailures);
    }
}
