using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// The link session sweeper must look for stale sessions once a minute and stop cleanly with the server.
/// </summary>
public sealed class LinkSessionSweeperTests
{
    /// <summary>
    /// The sweeper needs the sessions it sweeps and a logger.
    /// True: a missing dependency shows at construction.
    /// False: the service would start and then fail on its first tick.
    /// </summary>
    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        var sessions = Sessions(out _);

        Assert.Throws<ArgumentNullException>(() => new LinkSessionSweeper(null!, NullLogger<LinkSessionSweeper>.Instance));
        Assert.Throws<ArgumentNullException>(() => new LinkSessionSweeper(sessions, null!));
    }

    /// <summary>
    /// Stale sessions are looked for every minute.
    /// True: a viewer whose stop was lost is cleared within a minute of going stale.
    /// False: a changed interval would leave stale viewers listed, or sweep far too often.
    /// </summary>
    [Fact]
    public void Every_IsOneMinute()
    {
        var every = LinkSessionSweeper.Every;

        Assert.Equal(TimeSpan.FromMinutes(1), every);
    }

    /// <summary>
    /// A sweeper stopped before its first tick ends without sweeping and without an error.
    /// True: a server shutting down is not held up or made to log a failure.
    /// False: stopping would throw a cancellation out of the hosted service.
    /// </summary>
    [Fact]
    public async Task StopAsync_BeforeTheFirstTick_EndsWithoutSweeping()
    {
        using var sweeper = new LinkSessionSweeper(Sessions(out var manager), NullLogger<LinkSessionSweeper>.Instance);
        await sweeper.StartAsync(CancellationToken.None);

        await sweeper.StopAsync(CancellationToken.None);

        Assert.True(sweeper.ExecuteTask!.IsCompleted);
        Assert.False(sweeper.ExecuteTask.IsFaulted);
        Assert.Empty(manager.Calls);
    }

    private static LinkPlaybackSessions Sessions(out InterfaceStub manager)
    {
        var (sessionManager, stub) = InterfaceStub.Create<ISessionManager>();
        manager = stub;
        return new LinkPlaybackSessions(sessionManager, null!, null!, null!, null!, null!, NullLogger<LinkSessionController>.Instance);
    }
}
