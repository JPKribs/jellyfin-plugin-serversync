using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Peer;

[PluginService(ServiceLifetime.Singleton)]
public sealed class PeerItemLookup
{
    public const int MaxProviderIds = 16;

    private readonly ILibraryManager _libraryManager;

    public PeerItemLookup(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public static Dictionary<string, string>? CleanIds(Dictionary<string, string>? ids)
    {
        if (ids is null)
        {
            return null;
        }

        var clean = ids
            .Where(p => !string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value) && p.Key.Length <= 64 && p.Value.Length <= 128)
            .Take(MaxProviderIds)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        return clean.Count == 0 ? null : clean;
    }

    public PeerLookupResponse Find(PeerLookupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var item = FindItem(request);
        return item is null
            ? new PeerLookupResponse()
            : new PeerLookupResponse { Found = true, ItemId = item.Id.ToString("N", System.Globalization.CultureInfo.InvariantCulture), ItemType = item.GetType().Name };
    }

    private BaseItem? FindItem(PeerLookupRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Path))
        {
            var byPath = _libraryManager.FindByPath(request.Path, null);
            if (byPath is not null && (string.IsNullOrEmpty(request.ItemType) || string.Equals(byPath.GetType().Name, request.ItemType, StringComparison.Ordinal)))
            {
                return byPath;
            }
        }

        var hasKind = Enum.TryParse<BaseItemKind>(request.ItemType, out var kind);
        if (hasKind && CleanIds(request.ProviderIds) is { } ids && Query(kind, q => q.HasAnyProviderId = ids) is { } byIds)
        {
            return byIds;
        }

        if (hasKind && kind is BaseItemKind.Episode or BaseItemKind.Season && request.IndexNumber.HasValue && CleanIds(request.SeriesProviderIds) is { } seriesIds
            && Query(BaseItemKind.Series, q => q.HasAnyProviderId = seriesIds) is { } series)
        {
            return kind == BaseItemKind.Season
                ? Query(BaseItemKind.Season, q =>
                {
                    q.AncestorIds = new[] { series.Id };
                    q.IndexNumber = request.IndexNumber;
                })
                : Query(BaseItemKind.Episode, q =>
                {
                    q.AncestorIds = new[] { series.Id };
                    q.ParentIndexNumber = request.ParentIndexNumber;
                    q.IndexNumber = request.IndexNumber;
                });
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return null;
        }

        return request.ItemType switch
        {
            nameof(Person) => _libraryManager.GetPerson(request.Name),
            nameof(MediaBrowser.Controller.Entities.Audio.MusicArtist) => Query(BaseItemKind.MusicArtist, q => q.Name = request.Name),
            nameof(MediaBrowser.Controller.Entities.Movies.BoxSet) => Query(BaseItemKind.BoxSet, q => q.Name = request.Name),
            _ => null
        };
    }

    private BaseItem? Query(BaseItemKind kind, Action<InternalItemsQuery> narrow)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            Recursive = true,
            Limit = 1
        };
        narrow(query);
        var items = _libraryManager.GetItemList(query);
        return items.Count > 0 ? items[0] : null;
    }
}
