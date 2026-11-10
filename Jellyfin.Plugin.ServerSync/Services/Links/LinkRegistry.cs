using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// This server's links: the .strm files Server Sync wrote in place of a full copy. A link sits where the
/// copy would have gone, under the same name with only the extension changed, so a path that misses
/// here can still find its link. A .strm the user made is never a link.
/// </summary>
public interface ILinkRegistry
{
    /// <summary>
    /// The link standing in for a copy that would sit at <paramref name="copiedPath"/>, or null.
    /// </summary>
    string? LinkPathFor(string copiedPath);

    /// <summary>
    /// The path a full copy would have where <paramref name="linkPath"/> sits, with the source file's
    /// extension, or null when <paramref name="linkPath"/> is not one of this server's links.
    /// </summary>
    string? CopiedPathFor(string linkPath);

    /// <summary>
    /// The link at <paramref name="linkPath"/>, or null when that path is not one of this server's links.
    /// </summary>
    LinkEntry? Find(string linkPath) => null;

    /// <summary>Forgets what was read, so a link just written or removed is seen on the next lookup.</summary>
    void Invalidate()
    {
    }
}

/// <summary>
/// One of this server's links: where the .strm sits, where the copy would have gone, the id in its URL,
/// the server entry it streams from, and the file's size there, or 0 when unknown.
/// </summary>
public sealed record LinkEntry(string LinkPath, string CopiedPath, string LinkId, string? ServerKey, long Size = 0);

/// <summary>
/// No path is a link. Stands in where links do not matter, such as tests of the copy path.
/// </summary>
public sealed class NoLinks : ILinkRegistry
{
    public string? LinkPathFor(string copiedPath) => null;

    public string? CopiedPathFor(string linkPath) => null;
}

public static class LinkPaths
{
    public const string Extension = ".strm";

    /// <summary>
    /// Whether a source item can be held as a link. Jellyfin only follows a .strm for video. It serves
    /// an audio or book .strm as a plain file, which would hand a player the link's text, secret and
    /// all, so only video is ever linked.
    /// </summary>
    public static bool CanLink(Jellyfin.Sdk.Generated.Models.BaseItemDto item)
        => item?.Type is Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Movie
            or Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Episode
            or Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Video
            or Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.MusicVideo
            or Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Trailer;

    /// <summary>
    /// The other versions of a source film or episode, such as a 1080p file beside a 4K one, each as
    /// an item of its own with that version's id, path, and size. Jellyfin lists a film once, under
    /// its first version, with the rest as further media sources. A Link mapping links every version,
    /// which costs a small file each, so a user the source refuses one version can play another.
    /// Copies are made of the first version only, as they always were.
    /// </summary>
    /// <param name="item">The source item, as Jellyfin lists it.</param>
    /// <returns>One item per further version held as a file.</returns>
    public static IEnumerable<Jellyfin.Sdk.Generated.Models.BaseItemDto> OtherVersions(Jellyfin.Sdk.Generated.Models.BaseItemDto item)
    {
        if (item?.MediaSources is not { Count: > 1 } sources || !CanLink(item))
        {
            yield break;
        }

        foreach (var source in sources)
        {
            if (source is null
                || !Guid.TryParse(source.Id, out var versionId)
                || versionId == item.Id
                || string.IsNullOrEmpty(source.Path)
                || string.Equals(source.Path, item.Path, StringComparison.OrdinalIgnoreCase)
                || source.IsRemote == true
                || source.Path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return new Jellyfin.Sdk.Generated.Models.BaseItemDto
            {
                Id = versionId,
                Name = item.Name,
                Type = item.Type,
                Path = source.Path,
                DateCreated = item.DateCreated,
                MediaSources = new List<Jellyfin.Sdk.Generated.Models.MediaSourceInfo> { source }
            };
        }
    }

    /// <summary>
    /// Where a link for <paramref name="copiedPath"/> would sit, or null when the path has no extension
    /// to swap or is already a .strm.
    /// </summary>
    public static string? LinkPathFor(string? copiedPath)
    {
        if (string.IsNullOrEmpty(copiedPath))
        {
            return null;
        }

        var extension = Path.GetExtension(copiedPath);
        return string.IsNullOrEmpty(extension) || extension.Equals(Extension, StringComparison.OrdinalIgnoreCase)
            ? null
            : Path.ChangeExtension(copiedPath, Extension);
    }

    /// <summary>
    /// The path a full copy would have where a link row's .strm sits: the .strm path with the source
    /// file's extension. Null when the row has no local path.
    /// </summary>
    public static string? CopiedPathOf(Models.ContentSync.SyncItem row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return string.IsNullOrEmpty(row.LocalPath) ? null : Path.ChangeExtension(row.LocalPath, Path.GetExtension(row.SourcePath));
    }

    /// <summary>
    /// The path a table row records for a local item. A link records the path its copy would have, the
    /// same path a refresh translates from the source, so rows from either agree on which file they cover.
    /// </summary>
    public static string RowPathFor(string localPath, ILinkRegistry links)
        => links.CopiedPathFor(localPath) ?? localPath;

    /// <summary>
    /// Translates a local path back to the source. A link translates as the copy it stands in for, so it
    /// keeps the source file's real extension and the peer matches its own file by path.
    /// </summary>
    public static string SourcePathFor(string localPath, string localRoot, string sourceRoot, ILinkRegistry links)
        => PathUtilities.TranslatePath(RowPathFor(localPath, links), localRoot, sourceRoot);

    /// <summary>
    /// Adds each link in <paramref name="index"/> again under the path its copy would have, so a source
    /// path translated to this server finds it. An alias never replaces a path already in the index, so
    /// a real file always wins over a link standing in for the same name.
    /// </summary>
    public static void AddAliases(Dictionary<string, BaseItem> index, ILinkRegistry links)
    {
        foreach (var (path, item) in index.ToList())
        {
            if (links.CopiedPathFor(path) is { } copied)
            {
                index.TryAdd(copied, item);
            }
        }
    }
}
