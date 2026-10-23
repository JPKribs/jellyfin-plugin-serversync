using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

/// <summary>
/// The retry handler sits under every call to a source or peer server, so a
/// wrong retry either hides an outage or repeats a write.
/// </summary>
public class TransientRetryHandlerTests
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _steps;

        public ScriptedHandler(params Func<CancellationToken, Task<HttpResponseMessage>>[] steps)
        {
            _steps = new Queue<Func<CancellationToken, Task<HttpResponseMessage>>>(steps);
        }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return _steps.Dequeue()(cancellationToken);
        }
    }

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

    [Fact]
    public async Task DroppedConnection_IsRetried()
    {
        var inner = new ScriptedHandler(Drop(), Answer(HttpStatusCode.OK));
        var (client, _) = Build(inner);

        using var response = await client.GetAsync(new Uri("https://source.example/Items"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task SlowAttempt_IsCutOffAndRetried()
    {
        var inner = new ScriptedHandler(Hang(), Answer(HttpStatusCode.OK));
        var (client, _) = Build(inner, TimeSpan.FromMilliseconds(50));

        using var response = await client.GetAsync(new Uri("https://source.example/Items"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task EveryAttemptSlow_FailsAsATimeout()
    {
        var inner = new ScriptedHandler(Hang(), Hang(), Hang());
        var (client, _) = Build(inner, TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync(new Uri("https://source.example/Items")));
        Assert.Equal(TransientRetryHandler.MaxAttempts, inner.Calls);
    }

    [Fact]
    public async Task StillBusyAfterEveryAttempt_ReturnsTheLastAnswer()
    {
        var inner = new ScriptedHandler(Answer(HttpStatusCode.BadGateway), Answer(HttpStatusCode.BadGateway), Answer(HttpStatusCode.GatewayTimeout));
        var (client, _) = Build(inner);

        using var response = await client.GetAsync(new Uri("https://source.example/Items"));

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(TransientRetryHandler.MaxAttempts, inner.Calls);
    }

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
