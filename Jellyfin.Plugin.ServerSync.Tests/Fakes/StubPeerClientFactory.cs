using System;
using System.Collections.Generic;
using System.Net.Http;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Builds a real client over a <see cref="StubPeerHandler"/> for each server key, and records the keys it was asked for.
/// </summary>
internal sealed class StubPeerClientFactory : ISourceServerClientFactory
{
    /// <summary>
    /// Gets the handler serving each server key.
    /// </summary>
    public Dictionary<string, StubPeerHandler> Handlers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the server keys a client was built for, in order.
    /// </summary>
    public List<string> Created { get; } = new();

    /// <inheritdoc/>
    public SourceServerClient Create(SourceServer server)
    {
        Created.Add(server.Key);
        if (!Handlers.TryGetValue(server.Key, out var handler))
        {
            handler = new StubPeerHandler();
            Handlers[server.Key] = handler;
        }

        return new SourceServerClient(NullLogger<SourceServerClient>.Instance, new HttpClient(handler), server.Url, server.ApiKey, "test", "0.0.0-test");
    }
}
