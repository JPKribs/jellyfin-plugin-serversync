using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// This server's links, read from the content sync table. Lookups run on every path miss, so the table
/// is read once into a snapshot and read again when a link is written or removed, or after a short while.
/// </summary>
[PluginService(ServiceLifetime.Singleton, ServiceType = typeof(ILinkRegistry))]
public sealed class TableLinkRegistry : ILinkRegistry
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly object _loadLock = new();
    private volatile LoadedLinks? _snapshot;

    /// <summary>
    /// Initializes a new instance of the <see cref="TableLinkRegistry"/> class.
    /// </summary>
    /// <param name="services">Resolves the content sync table on first use.</param>
    public TableLinkRegistry(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <inheritdoc />
    public string? LinkPathFor(string copiedPath)
    {
        var candidate = LinkPaths.LinkPathFor(copiedPath);
        return candidate is not null
            && Snapshot().TryGetValue(candidate, out var entry)
            && string.Equals(entry.CopiedPath, copiedPath, StringComparison.OrdinalIgnoreCase)
                ? entry.LinkPath
                : null;
    }

    /// <inheritdoc />
    public string? CopiedPathFor(string linkPath) => Find(linkPath)?.CopiedPath;

    /// <inheritdoc />
    public LinkEntry? Find(string linkPath)
        => !string.IsNullOrEmpty(linkPath)
            && linkPath.EndsWith(LinkPaths.Extension, StringComparison.OrdinalIgnoreCase)
            && Snapshot().TryGetValue(linkPath, out var entry)
                ? entry
                : null;

    /// <inheritdoc />
    public void Invalidate() => _snapshot = null;

    /// <summary>
    /// Returns the links by path, reloading a stale snapshot under a lock so readers of a fresh one never wait on the database.
    /// </summary>
    private IReadOnlyDictionary<string, LinkEntry> Snapshot()
    {
        if (_snapshot is { } current && DateTime.UtcNow - current.LoadedAt < MaxAge)
        {
            return current.ByLinkPath;
        }

        lock (_loadLock)
        {
            if (_snapshot is { } loaded && DateTime.UtcNow - loaded.LoadedAt < MaxAge)
            {
                return loaded.ByLinkPath;
            }

            // A failed read is retried on the next lookup, never stored as no links.
            if (_services.GetRequiredService<ContentSyncTableManager>().TryGetLinks() is not { } rows)
            {
                return _snapshot?.ByLinkPath ?? new Dictionary<string, LinkEntry>(StringComparer.OrdinalIgnoreCase);
            }

            var byLinkPath = new Dictionary<string, LinkEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (LinkPaths.CopiedPathOf(row) is { } copied)
                {
                    byLinkPath[row.LocalPath!] = new LinkEntry(row.LocalPath!, copied, row.SourceItemId, row.ServerKey, row.SourceSize);
                }
            }

            _snapshot = new LoadedLinks(byLinkPath, DateTime.UtcNow);
            return byLinkPath;
        }
    }

    /// <summary>The links read from the table, and when they were read.</summary>
    private sealed record LoadedLinks(IReadOnlyDictionary<string, LinkEntry> ByLinkPath, DateTime LoadedAt);
}
