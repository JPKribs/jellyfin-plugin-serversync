using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>What identifies an item when asking another server for it.</summary>
/// <param name="ItemType">The item's type name.</param>
/// <param name="LocalPath">The item's path here, or null.</param>
/// <param name="Name">The item's name, or null.</param>
/// <param name="ProviderIds">The item's provider ids, or null.</param>
/// <param name="SeriesProviderIds">The provider ids of the item's series, or null.</param>
/// <param name="IndexNumber">The item's index, or null.</param>
/// <param name="ParentIndexNumber">The index of the item's season, or null.</param>
public sealed record PeerItemKeys(
    string ItemType,
    string? LocalPath,
    string? Name,
    Dictionary<string, string>? ProviderIds,
    Dictionary<string, string>? SeriesProviderIds,
    int? IndexNumber,
    int? ParentIndexNumber)
{
    /// <summary>Builds the keys from the lookup info Jellyfin gives a provider.</summary>
    /// <param name="itemType">The item's type name.</param>
    /// <param name="info">The lookup info.</param>
    /// <returns>The keys.</returns>
    public static PeerItemKeys FromInfo(string itemType, ItemLookupInfo info) => new(
        itemType,
        info.Path,
        info.Name,
        info.ProviderIds,
        info switch
        {
            EpisodeInfo episode => episode.SeriesProviderIds,
            SeasonInfo season => season.SeriesProviderIds,
            _ => null
        },
        info.IndexNumber,
        info.ParentIndexNumber);

    /// <summary>Builds the keys from a local item.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The keys.</returns>
    public static PeerItemKeys FromItem(BaseItem item) => new(
        item.GetType().Name,
        item.Path,
        item.Name,
        item.ProviderIds,
        item switch
        {
            MediaBrowser.Controller.Entities.TV.Episode episode => episode.Series?.ProviderIds,
            MediaBrowser.Controller.Entities.TV.Season season => season.Series?.ProviderIds,
            _ => null
        },
        item.IndexNumber,
        item.ParentIndexNumber);

    /// <summary>Names the item for a log line.</summary>
    /// <returns>The path, or the type and name when there is no path.</returns>
    public string Describe() => string.IsNullOrEmpty(LocalPath) ? ItemType + " " + Name : LocalPath;
}
