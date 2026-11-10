using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>Where a link streams from right now: a server entry here, and that server's id of the file.</summary>
public sealed record LinkRoute(string ServerKey, string ItemId);

/// <summary>Another server a link can fall back to, and the path the same file has there.</summary>
public sealed record LinkAlternate(SourceServer Server, string SourcePath);

/// <summary>
/// A link streams from the server that won its file in sync. When that server is down, full, or
/// refuses, the same file may be on another server this one pulls from: one that maps the folder the
/// link sits in. Those are tried in priority order, by the path the file would have there.
/// </summary>
public static class LinkFallback
{
    private static readonly char[] Separators = { '/', '\\' };

    /// <summary>The route a link streams from when nothing has moved it: the server that won its file.</summary>
    public static LinkRoute PrimaryOf(LinkEntry link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return new LinkRoute(link.ServerKey ?? string.Empty, link.LinkId);
    }

    /// <summary>
    /// The other servers this one pulls from whose enabled library mappings cover the folder a link
    /// sits in, highest priority first, each with the path the file would have there.
    /// </summary>
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
