using System;
using System.Collections.Generic;
using System.Net.Http;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Builds a real client over the HTTP handler a test set for each server key, and records the keys it was asked for.
/// A key with no handler fails the way a refused Server URL does.
/// </summary>
internal sealed class ProviderClientFactory : ISourceServerClientFactory
{
    /// <summary>Gets the handler that answers for each server key.</summary>
    public Dictionary<string, HttpMessageHandler> Handlers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the server keys a client was asked for, in order.</summary>
    public List<string> Created { get; } = new();

    /// <summary>Gets every client built, so a test can check which were disposed.</summary>
    public List<SourceServerClient> Clients { get; } = new();

    /// <inheritdoc/>
    public SourceServerClient Create(SourceServer server)
    {
        Created.Add(server.Key);
        if (!Handlers.TryGetValue(server.Key, out var handler))
        {
            throw new ArgumentException("Invalid source server URL: no handler for " + server.Key, nameof(server));
        }

        var client = new SourceServerClient(NullLogger<SourceServerClient>.Instance, new HttpClient(handler, disposeHandler: false), server.Url, server.ApiKey, "test", "0.0.0-test");
        Clients.Add(client);
        return client;
    }
}
