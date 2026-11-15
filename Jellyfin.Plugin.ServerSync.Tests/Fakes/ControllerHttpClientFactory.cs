using System.Collections.Generic;
using System.Net.Http;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Stands in for the named HTTP client factory behind a controller. Every client answers from the
/// handler a test supplies, and the names asked for are recorded.
/// </summary>
public sealed class ControllerHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    /// <summary>
    /// Initializes a new instance of the <see cref="ControllerHttpClientFactory"/> class.
    /// </summary>
    /// <param name="handler">Answers every request the built clients send.</param>
    public ControllerHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    /// <summary>
    /// Gets the client names asked for, in order.
    /// </summary>
    public List<string> Names { get; } = new();

    /// <inheritdoc/>
    public HttpClient CreateClient(string name)
    {
        Names.Add(name);
        return new HttpClient(_handler, disposeHandler: false);
    }
}
