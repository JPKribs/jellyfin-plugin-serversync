using System;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.ServerSync.Api;

/// <summary>
/// Reads the language a page asked in, so the text a route returns reads in the viewer's language.
/// </summary>
public static class RequestLanguage
{
    /// <summary>
    /// Gets the first language of a request's Accept-Language header, which the browser sends with every call.
    /// </summary>
    /// <param name="request">The request, or <c>null</c> when there is none, as in a test.</param>
    /// <returns>The language, such as de or pt-BR, or <c>null</c> for US English.</returns>
    public static string? Of(HttpRequest? request)
    {
        var header = request?.Headers.AcceptLanguage.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var first = header.Split(',')[0].Split(';')[0].Trim();
        return first.Length == 0 || first == "*" ? null : first;
    }
}
