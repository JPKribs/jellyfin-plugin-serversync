using System.Collections.Generic;
using System.Net.Http;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Builds a real client over one HTTP handler the test supplies, whichever server is asked for, and
/// records the keys of the servers it built a client for.
/// </summary>
internal sealed class LinkStubClientFactory : ISourceServerClientFactory
{
    private readonly HttpMessageHandler _handler;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkStubClientFactory"/> class.
    /// </summary>
    /// <param name="handler">Answers every request of every client.</param>
    public LinkStubClientFactory(HttpMessageHandler handler) => _handler = handler;

    /// <summary>
    /// Gets the server keys a client was built for, in order.
    /// </summary>
    public List<string> Created { get; } = new();

    /// <inheritdoc/>
    public SourceServerClient Create(SourceServer server)
    {
        Created.Add(server.Key);
        return new SourceServerClient(NullLogger<SourceServerClient>.Instance, new HttpClient(_handler, disposeHandler: false), server.Url, server.ApiKey, "test", "0.0.0-test");
    }
}
