using System;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>The source's decision on one link stream, and the item and user it was made for.</summary>
public sealed record LinkVerdict(bool Allowed, string? Reason, BaseItem? Item, Guid? UserId)
{
    public static LinkVerdict Refuse(string reason) => new(false, reason, null, null);
}

/// <summary>
/// Decides on the source whether a server may stream one of its files to one of its users through a
/// link. The source's own rules apply: the user it streams as must exist here, may play media, may see
/// the item, and stays within this server's limits for that server.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkPolicy
{
    // The client name Server Sync reports link playback under, so its sessions are not counted twice.
    public const string ClientName = "Server Sync";

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly LinkSessions _sessions;

    public LinkPolicy(ILibraryManager libraryManager, IUserManager userManager, ISessionManager sessionManager, LinkSessions sessions)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _sessionManager = sessionManager;
        _sessions = sessions;
    }

    /// <summary>
    /// Decides on one stream. A probe has no user: it reads the file for media info on behalf of the
    /// server, so only the server's own permission and the item apply, and it is not counted.
    /// </summary>
    /// <param name="peer">The calling server's entry here.</param>
    /// <param name="itemId">This server's id of the item.</param>
    /// <param name="remoteUserId">The user's id on the calling server.</param>
    /// <param name="claimedUserId">The user here the calling server maps them to, if it does.</param>
    /// <param name="probe">Whether this is the calling server's media probe.</param>
    /// <param name="watching">Whether someone is watching. A download into the calling server's cache is nobody watching, so the stream limits do not apply to it.</param>
    /// <returns>The verdict.</returns>
    public LinkVerdict Check(SourceServer peer, Guid itemId, string? remoteUserId, string? claimedUserId, bool probe, bool watching = true)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (!peer.IsEnabled || !peer.AllowLinking)
        {
            return LinkVerdict.Refuse($"linking is not allowed for {peer.DisplayName} here");
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || item is Folder || string.IsNullOrEmpty(item.Path) || !System.IO.File.Exists(item.Path))
        {
            return LinkVerdict.Refuse("the file is no longer on the source");
        }

        // A link here is not a file, and following it would chain one server's stream through another.
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

        if (peer.LinkHonorBitrateLimit
            && user.RemoteClientBitrateLimit is > 0 and var limit
            && item.TotalBitrate is > 0 and var bitrate
            && bitrate > limit)
        {
            return LinkVerdict.Refuse($"the file's bitrate is above the {limit / 1_000_000.0:0.#} Mbps the source allows your account");
        }

        // A stream already running is not a new one, so seeking or a second request never trips a limit.
        if (watching && !_sessions.IsActive(peer.Key, userId, item.Id))
        {
            if (peer.LinkMaxStreams > 0 && _sessions.CountForPeer(peer.Key) >= peer.LinkMaxStreams)
            {
                return LinkVerdict.Refuse($"the source allows {peer.LinkMaxStreams} link stream(s) at once and all are in use");
            }

            var most = peer.GetLinkMaxStreamsFor(userId);
            if (most > 0 && StreamsOf(userId, peer.LinkCountOwnSessions) >= most)
            {
                return LinkVerdict.Refuse($"the source allows your account {most} stream(s) at once and all are in use");
            }
        }

        return new LinkVerdict(true, null, item, userId);
    }

    // This server's own mapping for the calling server decides first, then the fallback user set here.
    // Only when this server maps no users and names no fallback is the calling server trusted to name
    // the user, since its key already holds access to this server. Otherwise no one streams.
    /// <summary>The user here a link stream for a user on the calling server runs as, or null for none.</summary>
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

        if (Guid.TryParse(peer.LinkFallbackUserId, out var fallback))
        {
            return fallback;
        }

        return peer.UserMappings?.Any(m => m.IsEnabled) != true && Guid.TryParse(claimedUserId, out var claimed) ? claimed : null;
    }

    /// <summary>The user's playback on this server outside links, when the setting counts it, otherwise 0.</summary>
    public int OwnPlayback(Guid userId, bool countOwnSessions)
        => countOwnSessions
            ? _sessionManager.Sessions.Count(s => s.UserId == userId
                && s.NowPlayingItem is not null
                && !string.Equals(s.Client, ClientName, StringComparison.Ordinal))
            : 0;

    private int StreamsOf(Guid userId, bool countOwnSessions)
        => _sessions.CountForUser(userId) + OwnPlayback(userId, countOwnSessions);
}
