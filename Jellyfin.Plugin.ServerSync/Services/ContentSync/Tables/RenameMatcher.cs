using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;

/// <summary>
/// Decides whether a new file on a source is an old one under a new name or in a new container, which the source gives a new id.
/// A row fits when its file sat in the same folder and either had the same size or is the same episode or the same film and version.
/// </summary>
public static class RenameMatcher
{
    /// <summary>Matches a provider id written in a path, such as [imdbid-tt0111161].</summary>
    private static readonly Regex PathProviderId = new(
        @"\[(?<provider>imdbid|tmdbid|tvdbid)-(?<id>[^\]]+)\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Says whether a row may be the old file a new source file replaced. The caller has already checked that both sit in the same source folder.
    /// </summary>
    /// <param name="newItem">The new item as the source returned it.</param>
    /// <param name="newSize">The new file's size in bytes.</param>
    /// <param name="row">The row of a file in the same folder.</param>
    /// <param name="local">This server's item for the row, or null when it has none.</param>
    /// <returns><c>true</c> when the row fits.</returns>
    public static bool Fits(BaseItemDto newItem, long newSize, SyncItem row, BaseItem? local)
    {
        ArgumentNullException.ThrowIfNull(newItem);
        ArgumentNullException.ThrowIfNull(row);

        // The same bytes under a new name.
        if (newSize > 0 && row.SourceSize == newSize)
        {
            return true;
        }

        return newItem.Type switch
        {
            BaseItemDto_Type.Episode => SameEpisode(newItem, local),
            BaseItemDto_Type.Movie => SameFilm(newItem, row, local),
            _ => false
        };
    }

    /// <summary>Picks the one row that fits, or null when none or more than one does.</summary>
    /// <param name="fitting">The rows that fit and whose file is gone from the source.</param>
    /// <returns>The row, or null.</returns>
    public static SyncItem? Single(IReadOnlyList<SyncItem> fitting)
    {
        ArgumentNullException.ThrowIfNull(fitting);
        return fitting.Count == 1 ? fitting[0] : null;
    }

    /// <summary>Says whether the new item is the same season and episode as this server's item.</summary>
    private static bool SameEpisode(BaseItemDto newItem, BaseItem? local)
        => local is MediaBrowser.Controller.Entities.TV.Episode episode
            && newItem.IndexNumber is { } number
            && episode.IndexNumber == number
            && episode.ParentIndexNumber == newItem.ParentIndexNumber
            && episode.IndexNumberEnd == newItem.IndexNumberEnd;

    /// <summary>Says whether the new item is the same film and the same version or edition as this server's item.</summary>
    private static bool SameFilm(BaseItemDto newItem, SyncItem row, BaseItem? local)
    {
        if (local is null || !string.Equals(VersionLabel(newItem.Path), VersionLabel(row.SourcePath), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var offered = ProviderIds(newItem);
        return local.ProviderIds is { Count: > 0 } held
            && offered.Any(pair => held.TryGetValue(pair.Key, out var value) && string.Equals(value, pair.Value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads the version or edition label of a film file, the text after the folder name and " - ", such as "4K" in "Film (2020)/Film (2020) - 4K.mkv".
    /// A file named otherwise has no label.
    /// </summary>
    /// <param name="path">The file's path.</param>
    /// <returns>The label, or empty.</returns>
    public static string VersionLabel(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var stem = Path.GetFileNameWithoutExtension(path);
        var folder = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
        var prefix = folder + " - ";
        return !string.IsNullOrEmpty(folder) && stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? stem[prefix.Length..].Trim()
            : string.Empty;
    }

    /// <summary>
    /// Reads the provider ids the source gave a new item, and any written in its path, since a film the source has just added may not be identified yet.
    /// </summary>
    /// <param name="item">The item as the source returned it.</param>
    /// <returns>The ids by provider name, such as Imdb.</returns>
    public static IReadOnlyDictionary<string, string> ProviderIds(BaseItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (item.ProviderIds?.AdditionalData is { } given)
        {
            foreach (var pair in given)
            {
                if (pair.Value is string value && !string.IsNullOrWhiteSpace(value))
                {
                    ids[pair.Key] = value;
                }
            }
        }

        foreach (Match match in PathProviderId.Matches(item.Path ?? string.Empty))
        {
            var provider = match.Groups["provider"].Value.ToLowerInvariant() switch
            {
                "imdbid" => "Imdb",
                "tmdbid" => "Tmdb",
                _ => "Tvdb"
            };
            ids.TryAdd(provider, match.Groups["id"].Value);
        }

        return ids;
    }
}
