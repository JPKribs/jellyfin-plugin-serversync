using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Clients;

/// <summary>
/// The retry handler sits under every call to a source or peer server, so a
/// wrong retry either hides an outage or repeats a write.
/// </summary>
public class TransientRetryHandlerTests
{
    private static Func<CancellationToken, Task<HttpResponseMessage>> Answer(HttpStatusCode status)
        => _ => Task.FromResult(new HttpResponseMessage(status));

    private static Func<CancellationToken, Task<HttpResponseMessage>> Drop()
        => _ => throw new HttpRequestException("test: connection reset");

    private static Func<CancellationToken, Task<HttpResponseMessage>> Hang()
        => async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        };

    private static (HttpClient Client, List<TimeSpan> Waits) Build(ScriptedHandler inner, TimeSpan? attemptTimeout = null)
    {
        var waits = new List<TimeSpan>();
        var handler = new TransientRetryHandler(
            (wait, _) =>
            {
                waits.Add(wait);
                return Task.CompletedTask;
            },
            attemptTimeout ?? TimeSpan.FromSeconds(30))
        {
            InnerHandler = inner
        };
        return (new HttpClient(handler), waits);
    }

    /// <summary>
    /// A busy answer is asked again after a wait.
    /// True: a 503 followed by a 200 returns the 200 after one wait.
    /// False: a server that is briefly busy would fail the whole sync step.
    /// </summary>
    [Fact]
    public async Task BusyServer_IsAskedAgain()
    {
        var inner = new ScriptedHandler(Answer(HttpStatusCode.ServiceUnavailable), Answer(HttpStatusCode.OK));
        var (client, waits) = Build(inner);

        using var response = await client.GetAsync(new Uri("https://source.example/Items"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);
        Assert.Single(waits);
    }

    /// <summary>
    /// A dropped connection is retried.
    /// True: a connection reset followed by a 200 returns the 200.
    /// False: one network blip would fail a scan.
    /// </summary>
    [Fact]
    public async Task DroppedConnection_IsRetried()
    {
        var inner = new ScriptedHandler(Drop(), Answer(HttpStatusCode.OK));
        var (client, _) = Build(inner);

        using var response = await client.GetAsync(new Uri("https://source.example/Items"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);
    }

    /// <summary>
    /// An attempt that exceeds the attempt timeout is cut off and retried.
    /// True: a hung first attempt is followed by a second that succeeds.
    /// False: a server that accepts a request and never answers would hang the sync.
    /// </summary>
    [Fact]
    public async Task SlowAttempt_IsCutOffAndRetried()
    {
        var inner = new ScriptedHandler(Hang(), Answer(HttpStatusCode.OK));
        var (client, _) = Build(inner, TimeSpan.FromMilliseconds(50));

        using var response = await client.GetAsync(new Uri("https://source.example/Items"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);
    }

    /// <summary>
    /// When every attempt times out the call fails as a timeout after the maximum attempts.
    /// True: a dead server fails the call after the allowed attempts and no more.
    /// False: a dead server would be retried forever, or fail without being retried.
    /// </summary>
    [Fact]
    public async Task EveryAttemptSlow_FailsAsATimeout()
    {
        var inner = new ScriptedHandler(Hang(), Hang(), Hang());
        var (client, _) = Build(inner, TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync(new Uri("https://source.example/Items")));
        Assert.Equal(TransientRetryHandler.MaxAttempts, inner.Calls);
    }

    /// <summary>
    /// A server that stays busy for every attempt returns its last answer.
    /// True: the caller sees the final gateway status after the maximum attempts.
    /// False: the caller would see an exception instead of the status, or attempts would exceed the maximum.
    /// </summary>
    [Fact]
    public async Task StillBusyAfterEveryAttempt_ReturnsTheLastAnswer()
    {
        var inner = new ScriptedHandler(Answer(HttpStatusCode.BadGateway), Answer(HttpStatusCode.BadGateway), Answer(HttpStatusCode.GatewayTimeout));
        var (client, _) = Build(inner);

        using var response = await client.GetAsync(new Uri("https://source.example/Items"));

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(TransientRetryHandler.MaxAttempts, inner.Calls);
    }

    /// <summary>
    /// A definite answer is returned at once.
    /// True: a 404, 401, or 500 is returned after one call.
    /// False: a missing item or bad key would be asked for three times, and a 500 would repeat work.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task DefiniteAnswers_AreNotRetried(HttpStatusCode status)
    {
        var inner = new ScriptedHandler(Answer(status));
        var (client, _) = Build(inner);

        using var response = await client.GetAsync(new Uri("https://source.example/Items"));

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    /// <summary>
    /// A write is sent once even when the server answers busy.
    /// True: a POST that gets a 503 is returned after one call.
    /// False: a retried write could apply twice on the peer.
    /// </summary>
    [Fact]
    public async Task Writes_AreNeverRetried()
    {
        var inner = new ScriptedHandler(Answer(HttpStatusCode.ServiceUnavailable));
        var (client, _) = Build(inner);

        using var content = new StringContent("{}");
        using var response = await client.PostAsync(new Uri("https://peer.example/Sync/Peer/Queue"), content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    /// <summary>
    /// A Retry-After longer than the cap waits only the cap.
    /// True: a ten minute request waits the maximum retry-after time.
    /// False: a server could stall a sync for as long as it asked.
    /// </summary>
    [Fact]
    public async Task RetryAfter_IsHonouredUpToTheCap()
    {
        var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
        var inner = new ScriptedHandler(_ => Task.FromResult(throttled), Answer(HttpStatusCode.OK));
        var (client, waits) = Build(inner);

        using var response = await client.GetAsync(new Uri("https://source.example/Items"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TransientRetryHandler.MaxRetryAfter, Assert.Single(waits));
    }

    /// <summary>
    /// A cancellation from the caller stops the call and is not retried.
    /// True: cancelling ends the call after one attempt.
    /// False: cancelling a task would be followed by more attempts.
    /// </summary>
    [Fact]
    public async Task CallerCancellation_IsNotRetried()
    {
        using var cts = new CancellationTokenSource();
        var inner = new ScriptedHandler(token =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var (client, _) = Build(inner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync(new Uri("https://source.example/Items"), cts.Token));
        Assert.Equal(1, inner.Calls);
    }
}
