using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Writes the .strm for a queued link row. Nothing is downloaded, so the copy path's disk, validation,
/// and temporary folder steps do not apply. A file that is not this row's link is never overwritten.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkWriter
{
    private readonly LinkFiles _files;
    private readonly ILinkRegistry _links;
    private readonly ILogger<LinkWriter> _logger;
    private bool _floorRaised;

    public LinkWriter(LinkFiles files, ILinkRegistry links, ILogger<LinkWriter> logger)
    {
        _files = files;
        _links = links;
        _logger = logger;
    }

    /// <summary>
    /// Writes the link, or throws with a reason the Sync tab shows when the path is taken.
    /// </summary>
    /// <param name="row">The link's row.</param>
    /// <param name="table">The content table, for path collisions.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="replacing">A kept copy this link takes over from, which may stay beside it until the link is in place.</param>
    public void Write(SyncItem row, ContentSyncTableManager table, PluginConfiguration config, string? replacing = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(config);
        var path = row.LocalPath;
        if (string.IsNullOrEmpty(path) || !path.EndsWith(LinkPaths.Extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The link has no .strm path (source: {row.SourcePath})");
        }

        // The same boundary a copy is held to: a link is only ever written inside a mapped library.
        if (!FileValidationService.IsPathWithinLibrary(Path.GetFullPath(path), config))
        {
            throw new InvalidOperationException($"{path} is outside every mapped library, so no link is written there");
        }

        // A real copy at the same name always wins over a link.
        if (LinkPaths.CopiedPathOf(row) is { } copied && File.Exists(copied)
            && !string.Equals(replacing, copied, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"A full copy is already at {copied}, so no link is written beside it");
        }

        if (table.CheckPathCollision(path, row.SourceItemId))
        {
            throw new InvalidOperationException($"Another source file would link to {path}. Two source files differ only by their extension, so only one of them can be linked");
        }

        var replace = false;
        if (File.Exists(path))
        {
            if (_files.IsCurrent(path, row.SourceItemId))
            {
                return;
            }

            // Only a file that points at this plugin's link route can be an older version of this link.
            if (!LinkFiles.PointsAtLinkRoute(LinkFiles.ReadUrl(path)))
            {
                throw new InvalidOperationException($"A .strm that Server Sync did not write is already at {path}, so it is left alone");
            }

            replace = true;
        }

        _files.Write(path, row.SourceItemId, replace);
        _links.Invalidate();

        // The refresh already stored this row as a link, so from the first link on, older builds that
        // would take a .strm for a broken copy no longer open the database.
        if (!_floorRaised)
        {
            table.RequireLinkAwareReaders();
            _floorRaised = true;
        }

        _logger.LogInformation("LINKED: {FileName} -> {LocalPath}", Path.GetFileName(row.SourcePath), path);
    }

    /// <summary>
    /// Removes a link once its copy is in place. A .strm that does not point at this plugin's link route
    /// is left alone.
    /// </summary>
    public void Remove(string path)
    {
        if (File.Exists(path) && LinkFiles.PointsAtLinkRoute(LinkFiles.ReadUrl(path)))
        {
            File.Delete(path);
            _logger.LogInformation("Removed the link {LocalPath}, now that its copy is in place", path);
        }

        _links.Invalidate();
    }
}
