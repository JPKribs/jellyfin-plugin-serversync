using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Services.Links;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Stands in for this server's links with the entries a test adds, and counts the lookups and the
/// invalidations it was asked for.
/// </summary>
internal sealed class LinkFakeRegistry : ILinkRegistry
{
    private readonly Dictionary<string, LinkEntry> _byLinkPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets how many times a link was looked up by its path.
    /// </summary>
    public int Finds { get; private set; }

    /// <summary>
    /// Gets how many times the registry was told to forget what it read.
    /// </summary>
    public int Invalidations { get; private set; }

    /// <summary>
    /// Adds a link.
    /// </summary>
    /// <param name="entry">The link the registry holds.</param>
    /// <returns>This registry, to add another.</returns>
    public LinkFakeRegistry Add(LinkEntry entry)
    {
        _byLinkPath[entry.LinkPath] = entry;
        return this;
    }

    /// <inheritdoc/>
    public string? LinkPathFor(string copiedPath)
        => _byLinkPath.Values.FirstOrDefault(e => string.Equals(e.CopiedPath, copiedPath, StringComparison.OrdinalIgnoreCase))?.LinkPath;

    /// <inheritdoc/>
    public string? CopiedPathFor(string linkPath)
        => _byLinkPath.TryGetValue(linkPath, out var entry) ? entry.CopiedPath : null;

    /// <inheritdoc/>
    public LinkEntry? Find(string linkPath)
    {
        Finds++;
        return linkPath is not null && _byLinkPath.TryGetValue(linkPath, out var entry) ? entry : null;
    }

    /// <inheritdoc/>
    public void Invalidate() => Invalidations++;
}
