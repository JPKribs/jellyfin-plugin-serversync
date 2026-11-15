using System;
using System.Collections.Generic;
using System.Net.Http;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Stands in for the client factory behind a controller. It builds a real client over the handler a
/// test supplies, records every entry it was asked for, and refuses with a reason when one is set.
/// </summary>
public sealed class ControllerClientFactory : ISourceServerClientFactory
{
    private readonly HttpMessageHandler _handler;

    /// <summary>
    /// Initializes a new instance of the <see cref="ControllerClientFactory"/> class.
    /// </summary>
    /// <param name="handler">Answers every request the built clients send.</param>
    public ControllerClientFactory(HttpMessageHandler handler) => _handler = handler;

    /// <summary>
    /// Gets the entries a client was asked for, in order.
    /// </summary>
    public List<SourceServer> Created { get; } = new();

    /// <summary>
    /// Gets or sets the reason the factory refuses every entry with, the way the SSRF guard does, or null to build clients.
    /// </summary>
    public string? Refusal { get; set; }

    /// <inheritdoc/>
    public SourceServerClient Create(SourceServer server)
    {
        Created.Add(server);
        if (Refusal is not null)
        {
            throw new ArgumentException(Refusal);
        }

        return new SourceServerClient(NullLogger<SourceServerClient>.Instance, new HttpClient(_handler, disposeHandler: false), server.Url, server.ApiKey, "test", "0.0.0-test");
    }
}
