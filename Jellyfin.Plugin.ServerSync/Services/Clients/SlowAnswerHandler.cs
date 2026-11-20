using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JPKribs.Jellyfin.Base;
using Microsoft.Kiota.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Services.Clients;

/// <summary>
/// Sets how long a source may take to start its answer.
/// A request marked with <see cref="SlowAnswerOption"/> is sent once with no retry and may take the client's whole time limit, <see cref="SlowLimit"/>.
/// Every other request keeps its retries and is cut off at <see cref="Limit"/>.
/// It must sit outside the retry handler, on a client whose own time limit is <see cref="SlowLimit"/>.
/// </summary>
public sealed class SlowAnswerHandler : DelegatingHandler
{
    /// <summary>How long an ordinary request may take to start its answer, every retry included.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    /// <summary>How long a marked request may take, which the client's own time limit enforces.</summary>
    public static readonly TimeSpan SlowLimit = TimeSpan.FromMinutes(30);

    /// <summary>The key the Jellyfin SDK files a request's <see cref="SlowAnswerOption"/> under.</summary>
    private static readonly HttpRequestOptionsKey<IRequestOption> Marked = new(typeof(SlowAnswerOption).FullName!);

    private readonly TimeSpan _limit;

    /// <summary>
    /// Initializes a new instance of the <see cref="SlowAnswerHandler"/> class.
    /// </summary>
    /// <param name="limit">How long an ordinary request may take, or null for <see cref="Limit"/>.</param>
    public SlowAnswerHandler(TimeSpan? limit = null)
    {
        _limit = limit ?? Limit;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_limit, TimeSpan.Zero);
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Three tries cut off at the retry's attempt limit can never succeed against a source that needs longer than that to answer.
        if (request.Options.TryGetValue(Marked, out var option) && option is SlowAnswerOption)
        {
            request.Options.Set(BaseRetryHandler.NoRetry, true);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        using var deadline = new CancellationTokenSource(_limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await base.SendAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The same shape the client gives its own time limit, which callers already read.
            var message = $"{request.RequestUri?.GetLeftPart(UriPartial.Path)} did not answer within {_limit.TotalSeconds:0} seconds";
            throw new TaskCanceledException(message, new TimeoutException(message, ex));
        }
    }
}
