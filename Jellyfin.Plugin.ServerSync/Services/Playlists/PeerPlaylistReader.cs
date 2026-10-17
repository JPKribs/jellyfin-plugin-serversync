using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Playlists;

[PluginService(ServiceLifetime.Singleton)]
public sealed class PeerPlaylistReader
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    public PeerPlaylistReader(ILibraryManager libraryManager, IUserManager userManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    public PeerPlaylistsResponse ReadAll(Guid? onlyId = null, Guid? scopedUserId = null, IReadOnlyCollection<Guid>? owners = null)
    {
        var scoped = scopedUserId is { } userId ? _userManager.GetUserById(userId) : null;
        if (scopedUserId is not null && scoped is null)
        {
            return new PeerPlaylistsResponse();
        }

        var playlists = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Playlist },
            Recursive = true
        }).OfType<Playlist>().Where(p => (onlyId is null || p.Id.Equals(onlyId.Value))
            && (scoped is null || p.IsVisible(scoped))
            && (owners is null || owners.Contains(p.OwnerUserId)));

        var response = new PeerPlaylistsResponse();
        foreach (var playlist in playlists)
        {
            response.Playlists.Add(new PeerPlaylist
            {
                Id = playlist.Id.ToString("N", CultureInfo.InvariantCulture),
                RootId = PlaylistMirror.RootKey(playlist),
                IsCopy = PlaylistMirror.IsMirror(playlist),
                Name = playlist.Name ?? string.Empty,
                MediaType = playlist.MediaType.ToString(),
                OwnerUserId = playlist.OwnerUserId.ToString("N", CultureInfo.InvariantCulture),
                OpenAccess = playlist.OpenAccess,
                Shares = playlist.Shares
                    .Select(s => new PeerPlaylistShare { UserId = s.UserId.ToString("N", CultureInfo.InvariantCulture), CanEdit = s.CanEdit })
                    .ToList(),
                Entries = playlist.GetManageableItems()
                    .Where(e => scoped is null || e.Item2.IsVisible(scoped))
                    .Select(e => new PeerPlaylistEntry { ItemId = e.Item2.Id.ToString("N", CultureInfo.InvariantCulture), Path = e.Item2.Path })
                    .ToList()
            });
        }

        return response;
    }
}
