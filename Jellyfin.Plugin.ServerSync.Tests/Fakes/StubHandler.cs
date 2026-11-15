using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// An HTTP handler that answers every request from a function the test supplies, and records the
/// requests and the bodies it was sent.
/// </summary>
public sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubHandler"/> class that answers each request with a function.
    /// </summary>
    /// <param name="answer">Builds the response for a request.</param>
    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubHandler"/> class that answers every request with a 200 and a body.
    /// </summary>
    /// <param name="body">Builds the stream sent as the response body, once per request.</param>
    public StubHandler(Func<Stream> body)
        : this(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body()) })
    {
    }

    /// <summary>
    /// Gets the number of requests received.
    /// </summary>
    public int Requests { get; private set; }

    /// <summary>
    /// Gets the body of each request received, empty for a request with none.
    /// </summary>
    public List<string> Bodies { get; } = new();

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return _answer(request);
    }
}
