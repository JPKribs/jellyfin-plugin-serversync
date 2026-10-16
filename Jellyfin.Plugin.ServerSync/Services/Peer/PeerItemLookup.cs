using System;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Peer;

[PluginService(ServiceLifetime.Singleton)]
public sealed class PeerItemLookup
{
    private readonly ILibraryManager _libraryManager;

    public PeerItemLookup(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
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

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return null;
        }

        return request.ItemType switch
        {
            nameof(Person) => _libraryManager.GetPerson(request.Name),
            nameof(MediaBrowser.Controller.Entities.Audio.MusicArtist) => ByName(BaseItemKind.MusicArtist, request.Name),
            nameof(MediaBrowser.Controller.Entities.Movies.BoxSet) => ByName(BaseItemKind.BoxSet, request.Name),
            _ => null
        };
    }

    private BaseItem? ByName(BaseItemKind kind, string name) => First(_libraryManager.GetItemList(new InternalItemsQuery
    {
        IncludeItemTypes = new[] { kind },
        Name = name,
        Recursive = true,
        Limit = 1
    }));

    private static BaseItem? First(System.Collections.Generic.IReadOnlyList<BaseItem> items) => items.Count > 0 ? items[0] : null;
}
