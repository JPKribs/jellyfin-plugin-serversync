using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JPKribs.Jellyfin.Base;
using Microsoft.Kiota.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Services.Clients;

/// <summary>
/// Turns the retry off for a request marked with <see cref="SlowAnswerOption"/>, so it is sent once and may take the client's whole time limit.
/// Three tries cut off at the retry's attempt limit can never succeed against a source that needs longer than that limit to answer.
/// It must sit outside the retry handler.
/// </summary>
public sealed class SlowAnswerHandler : DelegatingHandler
{
    /// <summary>The key the Jellyfin SDK files a request's <see cref="SlowAnswerOption"/> under.</summary>
    private static readonly HttpRequestOptionsKey<IRequestOption> Marked = new(typeof(SlowAnswerOption).FullName!);

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Options.TryGetValue(Marked, out var option) && option is SlowAnswerOption)
        {
            request.Options.Set(JpkRetryHandler.NoRetry, true);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
