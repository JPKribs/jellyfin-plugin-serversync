using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Exponential-backoff retry wrapper for transient network failures.
/// </summary>
public static class RetryPolicy
{
    private const int BaseDelayMs = 1000;
    private const int MaxDelayMs = 30000;

    /// <summary>
    /// Runs <paramref name="operation"/>, retrying transient failures up to
    /// <paramref name="maxRetries"/> times with exponential backoff and jitter.
    /// </summary>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        int maxRetries,
        ILogger logger,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        var attempt = 0;
        Exception? lastException = null;

        while (attempt <= maxRetries)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransientException(ex))
            {
                lastException = ex;
                attempt++;

                if (attempt > maxRetries)
                {
                    logger.LogError(ex, "{Operation} failed after {Attempts} attempts", operationName, attempt);
                    throw;
                }

                var delay = CalculateDelay(attempt);
                logger.LogWarning(
                    "{Operation} failed (attempt {Attempt}/{MaxRetries}), retrying in {Delay}ms: {Error}",
                    operationName, attempt, maxRetries + 1, delay, ex.Message);

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
            catch (Exception)
            {
                throw;
            }
        }

        throw lastException ?? new InvalidOperationException("Retry failed without exception");
    }

    /// <summary>
    /// Void-returning overload of <see cref="ExecuteWithRetryAsync{T}"/>.
    /// </summary>
    public static async Task ExecuteWithRetryAsync(
        Func<CancellationToken, Task> operation,
        int maxRetries,
        ILogger logger,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithRetryAsync(
            async ct =>
            {
                await operation(ct).ConfigureAwait(false);
                return true;
            },
            maxRetries,
            logger,
            operationName,
            cancellationToken).ConfigureAwait(false);
    }

    private static int CalculateDelay(int attempt)
    {
        // Exponential backoff capped at MaxDelayMs; exponent clamped to avoid int overflow.
        var clampedExponent = Math.Min(attempt - 1, 20);
        var exponentialDelay = (int)(BaseDelayMs * Math.Pow(2, clampedExponent));
        var cappedDelay = Math.Min(exponentialDelay, MaxDelayMs);

        // ±25% jitter to prevent thundering herd.
        var jitter = Random.Shared.Next(-(cappedDelay / 4), cappedDelay / 4);
        return cappedDelay + jitter;
    }

    private static bool IsTransientException(Exception ex)
    {
        if (ex is HttpRequestException httpEx)
        {
            if (httpEx.StatusCode.HasValue)
            {
                return IsTransientStatusCode(httpEx.StatusCode.Value);
            }

            return true;
        }

        if (ex is TaskCanceledException taskCanceledEx && taskCanceledEx.InnerException is TimeoutException)
        {
            return true;
        }

        // Generic IO is transient, but specific subtypes (not found, path-too-long) are not.
        if (ex is System.IO.IOException
            and not System.IO.FileNotFoundException
            and not System.IO.DirectoryNotFoundException
            and not System.IO.PathTooLongException
            and not System.IO.DriveNotFoundException)
        {
            return true;
        }

        if (ex is System.Net.Sockets.SocketException)
        {
            return true;
        }

        if (ex.InnerException != null)
        {
            return IsTransientException(ex.InnerException);
        }

        return false;
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.RequestTimeout => true,
            HttpStatusCode.TooManyRequests => true,
            HttpStatusCode.InternalServerError => true,
            HttpStatusCode.BadGateway => true,
            HttpStatusCode.ServiceUnavailable => true,
            HttpStatusCode.GatewayTimeout => true,
            _ => false
        };
    }
}
