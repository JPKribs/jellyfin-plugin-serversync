using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Finds the other servers a link can stream from when the server that won its file is down, full, or
/// refuses. They are the pull servers that map the folder the link sits in, tried in rank order.
/// </summary>
public static class LinkFallback
{
    private static readonly char[] Separators = { '/', '\\' };

    /// <summary>The route a link streams from when nothing has moved it, the server that won its file.</summary>
    /// <param name="link">The link to route.</param>
    /// <returns>The route.</returns>
    public static LinkRoute PrimaryOf(LinkEntry link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return new LinkRoute(link.ServerKey ?? string.Empty, link.LinkId);
    }

    /// <summary>
    /// The other servers this one pulls from whose enabled library mappings cover the folder a link
    /// sits in, highest ranked first, each with the path the file would have there.
    /// </summary>
    /// <param name="config">The configuration that lists the pull servers and their library mappings.</param>
    /// <param name="link">The link to find other servers for.</param>
    /// <returns>The other servers, highest ranked first.</returns>
    public static IEnumerable<LinkAlternate> Alternates(PluginConfiguration config, LinkEntry link)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(link);
        foreach (var server in config.GetPullServers())
        {
            if (string.Equals(server.Key, link.ServerKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = server.GetEnabledLibraryMappings()
                .Select(m => SourcePathFor(link.CopiedPath, m.LocalRootPath, m.SourceRootPath))
                .FirstOrDefault(p => p is not null);
            if (path is not null)
            {
                yield return new LinkAlternate(server, path);
            }
        }
    }

    /// <summary>
    /// The path a local file has on a server whose library at <paramref name="sourceRoot"/> maps to
    /// <paramref name="localRoot"/> here, written with that server's separator, or null when the file
    /// is not under <paramref name="localRoot"/>.
    /// </summary>
    /// <param name="localPath">The local file's path, or null.</param>
    /// <param name="localRoot">The local library root, or null.</param>
    /// <param name="sourceRoot">The source library root, or null.</param>
    /// <returns>The source path, or null.</returns>
    public static string? SourcePathFor(string? localPath, string? localRoot, string? sourceRoot)
    {
        localRoot = localRoot?.TrimEnd(Separators);
        sourceRoot = sourceRoot?.TrimEnd(Separators);
        if (string.IsNullOrEmpty(localPath) || string.IsNullOrEmpty(localRoot) || string.IsNullOrEmpty(sourceRoot)
            || localPath.Length <= localRoot.Length + 1
            || !localPath.StartsWith(localRoot, StringComparison.OrdinalIgnoreCase)
            || Array.IndexOf(Separators, localPath[localRoot.Length]) < 0)
        {
            return null;
        }

        var parts = localPath[(localRoot.Length + 1)..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or ".."))
        {
            return null;
        }

        var separator = sourceRoot.Contains('\\', StringComparison.Ordinal) && !sourceRoot.Contains('/', StringComparison.Ordinal) ? '\\' : '/';
        return sourceRoot + separator + string.Join(separator, parts);
    }
}
