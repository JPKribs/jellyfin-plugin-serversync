using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// A source server over a stub HTTP handler for the task tests. It answers the connection test like a
/// live server, answers every other path from a table the test fills, and records each request.
/// </summary>
public sealed class TaskSourceHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the status the connection test receives.
    /// </summary>
    public HttpStatusCode SystemInfoStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>
    /// Gets each request received, as the method, a space, and the path with its query.
    /// </summary>
    public List<string> Requests { get; } = new();

    /// <summary>
    /// Gets the body of each request received, empty for a request with none.
    /// </summary>
    public List<string> Bodies { get; } = new();

    /// <summary>
    /// Builds a 200 response with a JSON body.
    /// </summary>
    /// <param name="body">The JSON to send.</param>
    /// <returns>The response.</returns>
    public static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>
    /// Answers a path with a function of the request.
    /// </summary>
    /// <param name="path">The absolute path to answer, without its query.</param>
    /// <param name="answer">Builds the response for a request to that path.</param>
    /// <returns>This handler, so routes can be chained.</returns>
    public TaskSourceHandler On(string path, Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        _routes[path] = answer;
        return this;
    }

    /// <summary>
    /// Answers a path with a 200 and a JSON body.
    /// </summary>
    /// <param name="path">The absolute path to answer, without its query.</param>
    /// <param name="body">The JSON to send.</param>
    /// <returns>This handler, so routes can be chained.</returns>
    public TaskSourceHandler OnJson(string path, string body) => On(path, _ => Json(body));

    /// <summary>
    /// Counts the requests received for a path.
    /// </summary>
    /// <param name="path">The absolute path to count, without its query.</param>
    /// <returns>How many requests named that path.</returns>
    public int CountOf(string path)
    {
        lock (Requests)
        {
            var count = 0;
            foreach (var request in Requests)
            {
                var target = request[(request.IndexOf(' ', StringComparison.Ordinal) + 1)..];
                var query = target.IndexOf('?', StringComparison.Ordinal);
                if (string.Equals(query < 0 ? target : target[..query], path, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        lock (Requests)
        {
            Requests.Add(request.Method.Method + " " + request.RequestUri.PathAndQuery);
            Bodies.Add(body);
        }

        if (_routes.TryGetValue(path, out var answer))
        {
            return answer(request);
        }

        return path switch
        {
            "/System/Info" => SystemInfoStatus == HttpStatusCode.OK
                ? Json("{\"ServerName\":\"source\",\"Id\":\"0123456789abcdef0123456789abcdef\"}")
                : new HttpResponseMessage(SystemInfoStatus),
            "/Users/Me" => new HttpResponseMessage(HttpStatusCode.BadRequest),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }
}
