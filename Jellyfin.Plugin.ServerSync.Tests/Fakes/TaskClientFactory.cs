using System;
using System.Collections.Generic;
using System.Net.Http;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Builds a real client over a <see cref="TaskSourceHandler"/> for each server key, refuses the keys a
/// test marks as rejected, and records the keys it was asked for.
/// </summary>
public sealed class TaskClientFactory : ISourceServerClientFactory
{
    /// <summary>
    /// Gets the handler serving each server key.
    /// </summary>
    public Dictionary<string, TaskSourceHandler> Handlers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the server keys whose client cannot be built, as a bad Server URL is refused.
    /// </summary>
    public HashSet<string> Rejected { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the server keys a client was asked for, in order.
    /// </summary>
    public List<string> Created { get; } = new();

    /// <summary>
    /// Gets the handler for a server key, adding one when none exists.
    /// </summary>
    /// <param name="serverKey">The server entry key.</param>
    /// <returns>The handler that serves that key.</returns>
    public TaskSourceHandler HandlerFor(string serverKey)
    {
        if (!Handlers.TryGetValue(serverKey, out var handler))
        {
            handler = new TaskSourceHandler();
            Handlers[serverKey] = handler;
        }

        return handler;
    }

    /// <summary>
    /// Builds a client outside a run, for a test that hands a source to a task itself.
    /// </summary>
    /// <param name="server">The server entry the client is bound to.</param>
    /// <returns>The client.</returns>
    public SourceServerClient ClientFor(SourceServer server)
        => new(NullLogger<SourceServerClient>.Instance, new HttpClient(HandlerFor(server.Key)), server.Url, server.ApiKey, "test", "0.0.0-test");

    /// <inheritdoc/>
    public SourceServerClient Create(SourceServer server)
    {
        Created.Add(server.Key);
        if (Rejected.Contains(server.Key))
        {
            throw new ArgumentException("This test refuses the Server URL.", nameof(server));
        }

        return ClientFor(server);
    }
}
