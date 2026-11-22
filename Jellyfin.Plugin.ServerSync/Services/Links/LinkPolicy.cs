using System;
using System.IO;
using System.Linq;
using JPKribs.Jellyfin.Base;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Decides on the source whether a server may stream one of its files to one of its users through a
/// link. The source's own rules apply: the user it streams as must exist here, may play media, may see
/// the item, and stays within this server's limits for that server.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkPolicy
{
    /// <summary>The client name Server Sync reports link playback under, so its sessions are not counted twice.</summary>
    public const string ClientName = "Server Sync";

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly LinkSessions _sessions;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkPolicy"/> class.
    /// </summary>
    /// <param name="libraryManager">Finds the item a stream asks for.</param>
    /// <param name="userManager">Finds the user a stream runs as.</param>
    /// <param name="sessionManager">Counts the user's own playback here.</param>
    /// <param name="sessions">Counts the link streams this server is serving.</param>
    public LinkPolicy(ILibraryManager libraryManager, IUserManager userManager, ISessionManager sessionManager, LinkSessions sessions)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(sessions);
        _libraryManager = libraryManager;
        _userManager = userManager;
        _sessionManager = sessionManager;
        _sessions = sessions;
    }

    /// <summary>
    /// Decides on one stream. A probe has no user, so only the calling server's own permission and the
    /// item apply, and it is not counted.
    /// </summary>
    /// <param name="peer">The calling server's entry here.</param>
    /// <param name="itemId">This server's id of the item.</param>
    /// <param name="remoteUserId">The user's id on the calling server.</param>
    /// <param name="claimedUserId">The user here the calling server maps them to, if it does.</param>
    /// <param name="probe">Whether this is the calling server's media probe.</param>
    /// <param name="watching">Whether someone is watching, since a download into the calling server's link cache is exempt from the stream limits.</param>
    /// <param name="download">Whether the user is downloading the file, which also needs download permission here.</param>
    /// <returns>The verdict, which names the item and the user when it allows the stream.</returns>
    public LinkVerdict Check(SourceServer peer, Guid itemId, string? remoteUserId, string? claimedUserId, bool probe, bool watching = true, bool download = false)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (!peer.OffersLinks)
        {
            return LinkVerdict.Refuse($"linking is not allowed for {peer.DisplayName} here");
        }

        if (download && probe)
        {
            return LinkVerdict.Refuse("a media probe cannot download a file");
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || item is Folder || string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
        {
            return LinkVerdict.Refuse("the file is no longer on the source");
        }

        // Following a link here would chain one server's stream through another.
        if (item.Path.EndsWith(LinkPaths.Extension, StringComparison.OrdinalIgnoreCase))
        {
            return LinkVerdict.Refuse("the item is a link on the source, not a file");
        }

        if (probe && string.IsNullOrEmpty(remoteUserId))
        {
            return new LinkVerdict(true, null, item, null);
        }

        if (UserFor(peer, remoteUserId, claimedUserId) is not { } userId)
        {
            return LinkVerdict.Refuse("your account is not mapped to a user on the source");
        }

        var user = _userManager.GetUserById(userId);
        if (user is null || user.HasPermission(PermissionKind.IsDisabled))
        {
            return LinkVerdict.Refuse("the account you stream as on the source is missing or disabled");
        }

        if (!user.HasPermission(PermissionKind.EnableMediaPlayback))
        {
            return LinkVerdict.Refuse("the account you stream as on the source may not play media");
        }

        if (!item.IsVisible(user))
        {
            return LinkVerdict.Refuse("the account you stream as on the source cannot see this item");
        }

        if (download && !user.HasPermission(PermissionKind.EnableContentDownloading))
        {
            return LinkVerdict.Refuse("the account you stream as on the source may not download media");
        }

        if (peer.LinkHonorBitrateLimit
            && user.RemoteClientBitrateLimit is > 0 and var limit
            && item.TotalBitrate is > 0 and var bitrate
            && bitrate > limit)
        {
            return LinkVerdict.Refuse($"the file's bitrate is above the {limit / 1_000_000.0:0.#} Mbps the source allows your account");
        }

        // A running stream is not a new one, so seeking never trips a limit.
        if (watching && !_sessions.IsActive(peer.Key, userId, item.Id))
        {
            if (peer.LinkMaxStreams > 0 && _sessions.CountForPeer(peer.Key) >= peer.LinkMaxStreams)
            {
                return LinkVerdict.Refuse($"the source allows {ActivityText.Count(peer.LinkMaxStreams, "link stream")} at once and all are in use");
            }

            var most = peer.GetLinkMaxStreamsFor(userId);
            if (most > 0 && StreamsOf(userId, peer.LinkCountOwnSessions) >= most)
            {
                return LinkVerdict.Refuse($"the source allows your account {ActivityText.Count(most, "stream")} at once and all are in use");
            }
        }

        return new LinkVerdict(true, null, item, userId);
    }

    /// <summary>
    /// The user here a link stream for a user on the calling server runs as, or null for none. This
    /// server's own user mapping decides first, then the user it sets for unmapped users.
    /// </summary>
    /// <param name="peer">The linking server's entry here, which holds the user mappings.</param>
    /// <param name="remoteUserId">The user's id on that server, or null.</param>
    /// <param name="claimedUserId">The user id the request named, which is never trusted.</param>
    /// <returns>The local user's id, or null.</returns>
    public static Guid? UserFor(SourceServer peer, string? remoteUserId, string? claimedUserId)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (Guid.TryParse(remoteUserId, out var remote))
        {
            var mapped = peer.UserMappings?.FirstOrDefault(m => m.IsEnabled && Guid.TryParse(m.SourceUserId, out var id) && id == remote);
            if (mapped is not null)
            {
                return Guid.TryParse(mapped.LocalUserId, out var local) ? local : null;
            }
        }

        // The claimed user is never taken, since it could name an administrator here.
        return Guid.TryParse(peer.LinkFallbackUserId, out var fallback) ? fallback : null;
    }

    /// <summary>The user's playback on this server outside links, when the setting counts it, otherwise 0.</summary>
    /// <param name="userId">The local user whose sessions are counted.</param>
    /// <param name="countOwnSessions">Whether the setting counts the user's own playback.</param>
    /// <returns>The number of sessions.</returns>
    public int OwnPlayback(Guid userId, bool countOwnSessions)
        => countOwnSessions
            ? _sessionManager.Sessions.Count(s => s.UserId == userId
                && s.NowPlayingItem is not null
                && !string.Equals(s.Client, ClientName, StringComparison.Ordinal))
            : 0;

    /// <summary>Counts a user's link streams, plus their own playback here when the setting counts it.</summary>
    private int StreamsOf(Guid userId, bool countOwnSessions)
        => _sessions.CountForUser(userId) + OwnPlayback(userId, countOwnSessions);
}
