using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// An HTTP handler that answers each request with the next step of a script and counts the requests it saw.
/// </summary>
public sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _steps;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptedHandler"/> class.
    /// </summary>
    /// <param name="steps">The answers, one per request, in order.</param>
    public ScriptedHandler(params Func<CancellationToken, Task<HttpResponseMessage>>[] steps)
    {
        _steps = new Queue<Func<CancellationToken, Task<HttpResponseMessage>>>(steps);
    }

    /// <summary>
    /// Gets how many requests the handler received.
    /// </summary>
    public int Calls { get; private set; }

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return _steps.Dequeue()(cancellationToken);
    }
}
