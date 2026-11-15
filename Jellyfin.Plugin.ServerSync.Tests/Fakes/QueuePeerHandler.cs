using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// A peer over a stub HTTP handler. It answers each path from a table, answers 404 to the rest, and records every request.
/// </summary>
public sealed class QueuePeerHandler : HttpMessageHandler
{
    private static readonly JsonSerializerOptions PeerJson = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly Dictionary<string, Func<string, HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets every request received, as its method, path, query, and body.</summary>
    public List<(string Method, string Path, string Query, string Body)> Requests { get; } = new();

    /// <summary>Builds a 200 answer that carries a value as JSON, with enums as names.</summary>
    /// <param name="value">The value to send.</param>
    /// <returns>The answer.</returns>
    public static HttpResponseMessage Json(object value) => JsonText(JsonSerializer.Serialize(value, PeerJson));

    /// <summary>Builds a 200 answer that carries JSON text.</summary>
    /// <param name="json">The JSON to send.</param>
    /// <returns>The answer.</returns>
    public static HttpResponseMessage JsonText(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Reads a request body the way the peer routes do.</summary>
    /// <typeparam name="T">The request type.</typeparam>
    /// <param name="body">The body text.</param>
    /// <returns>The request.</returns>
    public static T Read<T>(string body) => JsonSerializer.Deserialize<T>(body, PeerJson)!;

    /// <summary>Answers a path from a function of the request body. A path that ends in a slash matches every path under it.</summary>
    /// <param name="path">The path to answer.</param>
    /// <param name="answer">Builds the answer from the request body.</param>
    /// <returns>This handler.</returns>
    public QueuePeerHandler On(string path, Func<string, HttpResponseMessage> answer)
    {
        _routes[path] = answer;
        return this;
    }

    /// <summary>Answers a path with a value as JSON.</summary>
    /// <param name="path">The path to answer.</param>
    /// <param name="value">The value to send.</param>
    /// <returns>This handler.</returns>
    public QueuePeerHandler OnJson(string path, object value) => On(path, _ => Json(value));

    /// <summary>Answers a path with a status and a body.</summary>
    /// <param name="path">The path to answer.</param>
    /// <param name="status">The status to answer.</param>
    /// <param name="body">The body text.</param>
    /// <returns>This handler.</returns>
    public QueuePeerHandler OnStatus(string path, HttpStatusCode status, string body = "")
        => On(path, _ => new HttpResponseMessage(status) { Content = new StringContent(body) });

    /// <summary>Fails a path the way an unreachable peer does.</summary>
    /// <param name="path">The path that fails.</param>
    /// <returns>This handler.</returns>
    public QueuePeerHandler OnUnreachable(string path) => On(path, _ => throw new HttpRequestException("the peer cannot be reached"));

    /// <summary>Counts the requests a path received.</summary>
    /// <param name="path">The path to count.</param>
    /// <returns>How many requests.</returns>
    public int CountOf(string path) => Requests.Count(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Gives the body of the last request a path received.</summary>
    /// <param name="path">The path to read.</param>
    /// <returns>The body, or null when the path was never asked.</returns>
    public string? LastBody(string path)
        => Requests.Where(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)).Select(r => r.Body).LastOrDefault();

    /// <summary>Builds a real client that talks to this peer.</summary>
    /// <param name="url">The address the client is given.</param>
    /// <returns>The client.</returns>
    public SourceServerClient Client(string url = "http://peer:8096")
        => new(NullLogger<SourceServerClient>.Instance, new HttpClient(this, disposeHandler: false), url, "key", "test", "0.0.0-test");

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add((request.Method.Method, path, request.RequestUri.Query, body));
        if (_routes.TryGetValue(path, out var exact))
        {
            return exact(body);
        }

        var under = _routes.Keys.FirstOrDefault(route => route.EndsWith('/') && path.StartsWith(route, StringComparison.OrdinalIgnoreCase));
        return under is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : _routes[under](body);
    }
}
