using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ServerSync.Services.Clients;

/// <summary>
/// Retries a read that got no answer within <see cref="AttemptTimeout"/>, lost its connection, or met a busy
/// server. Only GET and HEAD requests without a body are retried, since they change nothing on the other side.
/// </summary>
public sealed class TransientRetryHandler : DelegatingHandler
{
    /// <summary>How many times one request is sent at most.</summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// How long one attempt may wait for the answer's headers. The client's own timeout covers every attempt
    /// together, so one slow answer is cut off here and tried again.
    /// </summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(90);

    /// <summary>The longest wait a server's Retry-After can ask for.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _attemptTimeout;

    /// <summary>
    /// Initializes a new instance of the <see cref="TransientRetryHandler"/> class.
    /// </summary>
    public TransientRetryHandler()
        : this(Task.Delay, AttemptTimeout)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TransientRetryHandler"/> class.
    /// </summary>
    /// <param name="delay">Waits between attempts.</param>
    /// <param name="attemptTimeout">How long one attempt may wait for headers.</param>
    public TransientRetryHandler(Func<TimeSpan, CancellationToken, Task> delay, TimeSpan attemptTimeout)
    {
        ArgumentNullException.ThrowIfNull(delay);

        _delay = delay;
        _attemptTimeout = attemptTimeout;
    }

    /// <summary>Whether a status means the server may answer if asked again shortly.</summary>
    /// <param name="status">The status a server answered with.</param>
    /// <returns><c>true</c> for a timeout, throttling, or a gateway or availability failure.</returns>
    public static bool IsTransient(HttpStatusCode status)
        => status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// Set on a request whose caller tries another server at once, such as a link stream.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> NoRetry = new("ServerSync.NoRetry");

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var retryable = request.Content is null && (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head)
            && !(request.Options.TryGetValue(NoRetry, out var once) && once);
        if (!retryable)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        for (var attempt = 1; ; attempt++)
        {
            var last = attempt >= MaxAttempts;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_attemptTimeout);
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (last)
                {
                    throw new TaskCanceledException($"{request.RequestUri?.GetLeftPart(UriPartial.Path)} did not answer within {_attemptTimeout.TotalSeconds:0} seconds, {MaxAttempts} times", ex);
                }

                await _delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }
            // No status means the connection failed. Anything the server answered is not retried.
            catch (HttpRequestException ex) when (!last && ex.StatusCode is null)
            {
                await _delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (last || !IsTransient(response.StatusCode))
            {
                return response;
            }

            var wait = RetryAfter(response) ?? Backoff(attempt);
            response.Dispose();
            await _delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gives how long to wait after a failed attempt, one second then three, with up to a quarter more at
    /// random so many workers do not retry together.
    /// </summary>
    /// <param name="attempt">The attempt that just failed, counting from one.</param>
    /// <returns>The time to wait.</returns>
    private static TimeSpan Backoff(int attempt)
    {
        var baseSeconds = attempt == 1 ? 1.0 : 3.0;
        return TimeSpan.FromSeconds(baseSeconds * (1 + (Random.Shared.NextDouble() / 4)));
    }

    /// <summary>
    /// Reads the wait a server asked for in Retry-After, capped at <see cref="MaxRetryAfter"/>.
    /// </summary>
    /// <param name="response">The response that asked for a retry.</param>
    /// <returns>The wait, or null when the header is missing or not in the future.</returns>
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        TimeSpan? wait = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (wait is not { } value || value <= TimeSpan.Zero)
        {
            return null;
        }

        return value > MaxRetryAfter ? MaxRetryAfter : value;
    }
}
