using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Utilities;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// The shows and films picked to always keep on a Link mapping. Their files are full copies in the
/// library, synced like any copy, rather than links. Everything else in the mapping stays a link.
/// </summary>
public static class AlwaysKeep
{
    /// <summary>
    /// Whether a source file sits under an item picked to always keep on its Link mapping: inside a
    /// picked show's folder, the picked film's file, or another version of a picked film.
    /// </summary>
    /// <param name="mapping">The library mapping the file comes through.</param>
    /// <param name="sourcePath">The file's path on the source.</param>
    /// <returns>True when the file is copied rather than linked.</returns>
    public static bool Covers(LibraryMapping mapping, string? sourcePath)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return mapping is { StorageMode: StorageMode.Link, PermanentItems.Count: > 0 }
            && !string.IsNullOrEmpty(sourcePath)
            && (PathUtilities.IsItemFiltered(sourcePath, mapping.SourceRootPath ?? string.Empty, LibraryFilterMode.Blacklist, mapping.PermanentItems)
                || mapping.PermanentItems.Any(pick => SameFilm(pick.Path, sourcePath)));
    }

    /// <summary>
    /// Whether two files are versions of one film: in the same folder, each named after it, as in
    /// <c>Film (2020)/Film (2020) - 4K.mkv</c> and <c>Film (2020)/Film (2020) - 1080p.mkv</c>. A film is
    /// picked by its first version's file, and picking it keeps every version.
    /// </summary>
    /// <param name="pickedPath">The path of the item that was picked.</param>
    /// <param name="path">A link's source path.</param>
    /// <returns>True for two versions of one film.</returns>
    public static bool SameFilm(string? pickedPath, string? path)
    {
        if (string.IsNullOrEmpty(pickedPath) || string.IsNullOrEmpty(path) || !Path.HasExtension(pickedPath))
        {
            return false;
        }

        var picked = pickedPath.Replace('\\', '/');
        var other = path.Replace('\\', '/');
        var cut = picked.LastIndexOf('/');
        if (cut <= 0 || other.LastIndexOf('/') != cut || !other.StartsWith(picked[..(cut + 1)], StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var folder = picked[(picked.LastIndexOf('/', cut - 1) + 1)..cut];
        return folder.Length > 0
            && picked[(cut + 1)..].StartsWith(folder, StringComparison.OrdinalIgnoreCase)
            && other[(cut + 1)..].StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// How a source file is stored: as a copy when it is picked to always keep, otherwise as its
    /// mapping says.
    /// </summary>
    /// <param name="mapping">The library mapping the file comes through.</param>
    /// <param name="sourcePath">The file's path on the source.</param>
    /// <returns>The storage the file follows.</returns>
    public static StorageMode StorageFor(LibraryMapping mapping, string? sourcePath)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return Covers(mapping, sourcePath) ? StorageMode.Copy : mapping.StorageMode;
    }
}
