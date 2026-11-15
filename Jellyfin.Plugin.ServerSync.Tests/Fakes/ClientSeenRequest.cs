using System.Collections.Generic;
using System.Net.Http;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// One request a <see cref="ClientStubServer"/> received, copied out so a test can read it after the request is disposed.
/// </summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">The path, without the query.</param>
/// <param name="Query">The query string, unescaped, with its leading question mark, or empty.</param>
/// <param name="Authorization">The whole Authorization header, or null when there was none.</param>
/// <param name="Headers">Every request header by name, with several values joined by a comma.</param>
/// <param name="Body">The request body, or empty when there was none.</param>
/// <param name="NoRetry">Whether the request asked the retry handler to leave it alone.</param>
internal sealed record ClientSeenRequest(
    HttpMethod Method,
    string Path,
    string Query,
    string? Authorization,
    IReadOnlyDictionary<string, string> Headers,
    string Body,
    bool NoRetry)
{
    /// <summary>Reads one header.</summary>
    /// <param name="name">The header's name.</param>
    /// <returns>The value, or null when the request did not carry it.</returns>
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}
