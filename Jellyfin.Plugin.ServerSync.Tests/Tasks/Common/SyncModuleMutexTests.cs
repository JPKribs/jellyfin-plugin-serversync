using System;
using System.Threading;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Tasks.Common;

/// <summary>
/// Every caller that names a sync type receives the same single holder semaphore, so its scan and its sync never overlap.
/// </summary>
public sealed class SyncModuleMutexTests
{
    private static SemaphoreSlim ForModule(string? key)
        => (SemaphoreSlim)TaskInternals.Call("SyncModuleMutex", "ForModule", key)!;

    private static string UniqueKey() => "MutexTest-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Two requests for one key return one semaphore.
    /// True: a scan and a sync of the same sync type wait on each other.
    /// False: each task would hold its own lock and both would write the table at once.
    /// </summary>
    [Fact]
    public void ForModule_SameKey_ReturnsTheSameSemaphore()
    {
        var key = UniqueKey();

        var first = ForModule(key);
        var second = ForModule(key);

        Assert.Same(first, second);
    }

    /// <summary>
    /// Different keys return different semaphores.
    /// True: one sync type running never blocks another.
    /// False: a long Content run would stall History and Users.
    /// </summary>
    [Fact]
    public void ForModule_DifferentKeys_ReturnDifferentSemaphores()
    {
        var first = ForModule(UniqueKey());
        var second = ForModule(UniqueKey());

        Assert.NotSame(first, second);
    }

    /// <summary>
    /// Keys are matched by exact case.
    /// True: tasks must spell their key alike to share a lock, as every pair in the plugin does.
    /// False: a casing change in one task would be hidden while it quietly stopped sharing the lock.
    /// </summary>
    [Fact]
    public void ForModule_KeyDifferingOnlyInCase_IsAnotherSemaphore()
    {
        var key = UniqueKey();

        var lower = ForModule(key.ToLowerInvariant());
        var upper = ForModule(key.ToUpperInvariant());

        Assert.NotSame(lower, upper);
    }

    /// <summary>
    /// A semaphore admits one holder and admits the next once it is released.
    /// True: the second task waits and then runs.
    /// False: two holders would run together, or a release would never let the next one in.
    /// </summary>
    [Fact]
    public void ForModule_Semaphore_AdmitsOneHolderAtATime()
    {
        var semaphore = ForModule(UniqueKey());

        var first = semaphore.Wait(0);
        var second = semaphore.Wait(0);
        semaphore.Release();
        var third = semaphore.Wait(0);
        semaphore.Release();

        Assert.True(first);
        Assert.False(second);
        Assert.True(third);
    }

    /// <summary>
    /// A missing key is refused.
    /// True: a task with no key fails loudly at its first run.
    /// False: every task with a blank key would share one lock by accident.
    /// </summary>
    [Fact]
    public void ForModule_EmptyKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => ForModule(string.Empty));
    }

    /// <summary>
    /// A null key is refused.
    /// True: a task with no key fails loudly at its first run.
    /// False: the dictionary lookup would throw an unrelated error from inside the lock.
    /// </summary>
    [Fact]
    public void ForModule_NullKey_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ForModule(null));
    }

    /// <summary>
    /// A single row apply waits five seconds for the sync type.
    /// True: a hint gives up quickly and is put off while a scheduled run holds the lock.
    /// False: a hint would wait out a scan that can last an hour.
    /// </summary>
    [Fact]
    public void SingleRowWait_IsFiveSeconds()
    {
        var wait = (TimeSpan)TaskInternals.Field("SyncModuleMutex", "SingleRowWait")!;

        Assert.Equal(TimeSpan.FromSeconds(5), wait);
    }
}
