using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// A source server over a stub HTTP handler. It answers each path from a table the test fills, answers 404 for
/// the rest, records every request, and builds a real <see cref="SourceServerClient"/> that talks to it.
/// </summary>
internal sealed class ClientStubServer : HttpMessageHandler
{
    /// <summary>The Server URL the built client calls.</summary>
    public const string Url = "https://peer.example";

    private readonly Dictionary<string, Func<ClientSeenRequest, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    /// <summary>Gets every request received, in order.</summary>
    public List<ClientSeenRequest> Seen { get; } = new();

    /// <summary>Builds a JSON response.</summary>
    /// <param name="body">The JSON text.</param>
    /// <param name="status">The status to answer with.</param>
    /// <returns>The response.</returns>
    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Answers a path with a function.</summary>
    /// <param name="path">The path, without the query.</param>
    /// <param name="answer">Builds the response for a request.</param>
    /// <returns>This server.</returns>
    public ClientStubServer On(string path, Func<ClientSeenRequest, HttpResponseMessage> answer)
    {
        _routes[path] = answer;
        return this;
    }

    /// <summary>Answers a path with JSON.</summary>
    /// <param name="path">The path, without the query.</param>
    /// <param name="json">The JSON text.</param>
    /// <param name="status">The status to answer with.</param>
    /// <returns>This server.</returns>
    public ClientStubServer OnJson(string path, string json, HttpStatusCode status = HttpStatusCode.OK)
        => On(path, _ => Json(json, status));

    /// <summary>Answers a path with a status and an optional plain body.</summary>
    /// <param name="path">The path, without the query.</param>
    /// <param name="status">The status to answer with.</param>
    /// <param name="body">The body text, or null for none.</param>
    /// <returns>This server.</returns>
    public ClientStubServer OnStatus(string path, HttpStatusCode status, string? body = null)
        => On(path, _ => body is null ? new HttpResponseMessage(status) : new HttpResponseMessage(status) { Content = new StringContent(body) });

    /// <summary>Fails every request to a path the way a broken connection does.</summary>
    /// <param name="path">The path, without the query.</param>
    /// <param name="failure">The exception the handler throws.</param>
    /// <returns>This server.</returns>
    public ClientStubServer OnThrow(string path, Exception failure)
        => On(path, _ => throw failure);

    /// <summary>Lists the requests one path received.</summary>
    /// <param name="path">The path, without the query.</param>
    /// <returns>The requests, in order.</returns>
    public List<ClientSeenRequest> To(string path) => Seen.Where(r => r.Path == path).ToList();

    /// <summary>Builds a real client that sends every request to this server.</summary>
    /// <param name="serverUrl">The Server URL the client is given.</param>
    /// <param name="apiKey">The API Key the client sends.</param>
    /// <param name="localServerName">The name the client sends as its device.</param>
    /// <param name="pluginVersion">The plugin version the client sends.</param>
    /// <returns>The client.</returns>
    public SourceServerClient Client(string serverUrl = Url, string apiKey = "key", string localServerName = "test", string pluginVersion = "1.2.3.4")
        => new(NullLogger<SourceServerClient>.Instance, new HttpClient(this, disposeHandler: false), serverUrl, apiKey, localServerName, pluginVersion);

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri!;
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var seen = new ClientSeenRequest(
            request.Method,
            uri.AbsolutePath,
            Uri.UnescapeDataString(uri.Query),
            request.Headers.Authorization?.ToString(),
            headers,
            body,
            request.Options.TryGetValue(JPKribs.Jellyfin.Base.JpkRetryHandler.NoRetry, out var noRetry) && noRetry);
        lock (Seen)
        {
            Seen.Add(seen);
        }

        return _routes.TryGetValue(uri.AbsolutePath, out var answer) ? answer(seen) : new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
