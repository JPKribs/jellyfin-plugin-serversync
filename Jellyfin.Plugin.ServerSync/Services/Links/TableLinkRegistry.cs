using System;
using System.Collections.Generic;
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

    public TableLinkRegistry(IServiceProvider services)
    {
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

    // Readers never wait on the database while a fresh snapshot is in place. Only one caller reloads
    // at a time, and it swaps the whole snapshot in when done.
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

            var byLinkPath = new Dictionary<string, LinkEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _services.GetRequiredService<ContentSyncTableManager>().GetLinks())
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

    private sealed record LoadedLinks(IReadOnlyDictionary<string, LinkEntry> ByLinkPath, DateTime LoadedAt);
}
