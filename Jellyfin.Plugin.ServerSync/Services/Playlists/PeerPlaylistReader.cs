using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.DependencyInjection;

using Jellyfin.Plugin.ServerSync.Services.Links;

namespace Jellyfin.Plugin.ServerSync.Services.Playlists;

/// <summary>
/// Reads this server's playlists for a peer, with their shares and entries. An entry that is a link goes
/// out under the path of the file it stands in for.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class PeerPlaylistReader
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILinkRegistry? _links;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerPlaylistReader"/> class.
    /// </summary>
    /// <param name="libraryManager">Lists the playlists here.</param>
    /// <param name="userManager">Finds the user whose view limits the playlists.</param>
    /// <param name="links">Tells which paths are this server's links, or null to send every path as it is.</param>
    public PeerPlaylistReader(ILibraryManager libraryManager, IUserManager userManager, ILinkRegistry? links = null)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(userManager);
        _libraryManager = libraryManager;
        _userManager = userManager;
        _links = links;
    }

    /// <summary>Reads the playlists a caller may see.</summary>
    /// <param name="onlyId">The one playlist to read, or null for all.</param>
    /// <param name="scopedUserId">The user whose view limits the playlists and their entries, or null for no limit.</param>
    /// <param name="owners">Only playlists owned by these users, or null for any owner.</param>
    /// <returns>The playlists.</returns>
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
                    .Select(e => new PeerPlaylistEntry { ItemId = e.Item2.Id.ToString("N", CultureInfo.InvariantCulture), Path = SentPath(e.Item2.Path) })
                    .ToList()
            });
        }

        return response;
    }

    /// <summary>
    /// The path an entry goes out under. A link goes out under the path of the file it stands in for, so a
    /// peer matches its own copy of the film and never this server's .strm.
    /// </summary>
    private string SentPath(string path)
        => _links is null || string.IsNullOrEmpty(path) ? path : LinkPaths.RowPathFor(path, _links);
}
