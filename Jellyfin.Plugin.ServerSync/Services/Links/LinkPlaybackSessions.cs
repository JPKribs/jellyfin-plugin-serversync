using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Shows playback of a link on this server, the source, as a session of its own. Each viewer on the
/// server holding the link is a device here under the client name Server Sync, so the dashboard lists
/// it, the activity log and playback plugins see it, and Jellyfin's own stop handling updates played
/// state and resume position for the user it streams as. The server holding the link ends a session
/// by reporting its stop. When that report is lost, <see cref="SweepAsync"/> ends the session once it
/// has gone ten minutes without a report, since Jellyfin itself never removes it.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkPlaybackSessions
{
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<LinkSessionController> _controllerLogger;

    public LinkPlaybackSessions(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        IServerApplicationHost applicationHost,
        ILogger<LinkSessionController> controllerLogger)
    {
        _clientFactory = clientFactory;
        _applicationHost = applicationHost;
        _controllerLogger = controllerLogger;
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _configManager = configManager;
    }

    /// <summary>The device id a viewer on the calling server shows under here.</summary>
    public static string DeviceIdFor(SourceServer peer, string sessionKey)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(peer.Key + "|" + sessionKey)))[..24];
        return "serversync-link-" + hash.ToLowerInvariant();
    }

    /// <summary>Applies one report. Null when applied, otherwise why not.</summary>
    /// <param name="peer">The reporting server's entry here.</param>
    /// <param name="report">The report.</param>
    /// <param name="remoteEndPoint">The reporting server's address.</param>
    /// <returns>Null, or the refusal.</returns>
    public async Task<string?> ApplyAsync(SourceServer peer, PeerPlaybackReport report, string remoteEndPoint)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(report);
        if (!peer.OffersLinks)
        {
            return $"linking is not allowed for {peer.DisplayName} here";
        }

        if (!Guid.TryParse(report.ItemId, out var itemId) || _libraryManager.GetItemById(itemId) is not { } item)
        {
            return "no such item here";
        }

        if (LinkPolicy.UserFor(peer, report.UserId, report.SourceUserId) is not { } userId || _userManager.GetUserById(userId) is not { } user)
        {
            return "the user is not mapped to a user here";
        }

        // A report may only move played state the user could reach by playing the item here.
        if (!item.IsVisible(user))
        {
            return "the user cannot see that item here";
        }

        if (string.IsNullOrWhiteSpace(report.SessionKey))
        {
            return "the report names no session";
        }

        var session = await _sessionManager.LogSessionActivity(
            LinkPolicy.ClientName,
            _configManager.PluginVersion,
            DeviceIdFor(peer, report.SessionKey),
            DeviceName(peer, report),
            remoteEndPoint,
            user).ConfigureAwait(false);

        // The dashboard's remote control reaches the viewer through the server holding the link.
        var (controller, isNew) = session.EnsureController<LinkSessionController>(_ => new LinkSessionController(peer.Key, report.SessionKey, _configManager, _clientFactory, _applicationHost, _controllerLogger)).ToValueTuple();
        (controller as LinkSessionController)?.Touch();
        if (isNew)
        {
            _sessionManager.ReportCapabilities(null!, session.Id, new ClientCapabilities
            {
                PlayableMediaTypes = new[] { Jellyfin.Data.Enums.MediaType.Video, Jellyfin.Data.Enums.MediaType.Audio },
                SupportedCommands = new[] { GeneralCommandType.DisplayMessage },
                SupportsMediaControl = true
            });
        }

        var mediaSourceId = item.Id.ToString("N", CultureInfo.InvariantCulture);
        // This server only sends the file, so a transcode reported by an older linking server shows as
        // the direct stream it is here.
        var method = Enum.Parse<PlayMethod>(LinkPlaybackReporter.ReportedMethod(report.PlayMethod));
        switch (report.Event)
        {
            case LinkPlaybackEvent.Start:
                await _sessionManager.OnPlaybackStart(new PlaybackStartInfo
                {
                    ItemId = item.Id,
                    SessionId = session.Id,
                    MediaSourceId = mediaSourceId,
                    PositionTicks = report.PositionTicks,
                    IsPaused = report.IsPaused,
                    PlayMethod = method,
                    PlaySessionId = report.PlaySessionId,
                    CanSeek = true
                }).ConfigureAwait(false);
                break;
            case LinkPlaybackEvent.Progress:
                // A report for a viewer whose start was lost begins the playback here.
                if (session.NowPlayingItem?.Id != item.Id)
                {
                    goto case LinkPlaybackEvent.Start;
                }

                await _sessionManager.OnPlaybackProgress(new PlaybackProgressInfo
                {
                    ItemId = item.Id,
                    SessionId = session.Id,
                    MediaSourceId = mediaSourceId,
                    PositionTicks = report.PositionTicks,
                    IsPaused = report.IsPaused,
                    PlayMethod = method,
                    PlaySessionId = report.PlaySessionId,
                    CanSeek = true
                }).ConfigureAwait(false);
                break;
            case LinkPlaybackEvent.Stop:
                // The session ends even when Jellyfin's stop handling throws, and the stop is answered as
                // applied either way. Sent again, it would open a new session and stop it a second time.
                try
                {
                    await _sessionManager.OnPlaybackStopped(new PlaybackStopInfo
                    {
                        ItemId = item.Id,
                        SessionId = session.Id,
                        MediaSourceId = mediaSourceId,
                        PositionTicks = report.PositionTicks,
                        PlaySessionId = report.PlaySessionId
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _controllerLogger.LogWarning(ex, "Jellyfin could not stop the link session {Session}, so it is ended without its stop", session.Id);
                }

                await _sessionManager.ReportSessionEnded(session.Id).ConfigureAwait(false);
                break;
            default:
                return "unknown playback event";
        }

        return null;
    }

    /// <summary>
    /// Ends every link session that has had no report for <see cref="LinkSessionController.ActiveFor"/>.
    /// The server holding the link ends a session by reporting its stop, and when that report is lost
    /// nothing in Jellyfin removes the session: it only checks a session's controllers when a websocket
    /// closes, and a link session has none. So the sweep does it, stopping any playback first so played
    /// state and resume position are kept.
    /// </summary>
    /// <param name="now">The time to judge staleness by.</param>
    /// <returns>How many sessions were ended.</returns>
    public async Task<int> SweepAsync(DateTime now)
    {
        var ended = 0;
        foreach (var session in _sessionManager.Sessions.ToList())
        {
            var controllers = session.SessionControllers;
            if (controllers.Count == 0
                || !controllers.All(c => c is LinkSessionController link && !link.ActiveAt(now)))
            {
                continue;
            }

            try
            {
                if (session.NowPlayingItem is { } playing)
                {
                    await _sessionManager.OnPlaybackStopped(new PlaybackStopInfo
                    {
                        ItemId = playing.Id,
                        SessionId = session.Id,
                        MediaSourceId = session.PlayState?.MediaSourceId,
                        PositionTicks = session.PlayState?.PositionTicks is >= 0 and var at ? at : null
                    }).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _controllerLogger.LogDebug(ex, "Could not stop playback on the stale link session {Session}", session.Id);
            }

            try
            {
                await _sessionManager.ReportSessionEnded(session.Id).ConfigureAwait(false);
                ended++;
                _controllerLogger.LogInformation("Ended the link session {Device} for {User}: the server holding the link has not reported on it for {Minutes} minutes", session.DeviceName, session.UserName, LinkSessionController.ActiveFor.TotalMinutes);
            }
            catch (Exception ex)
            {
                _controllerLogger.LogWarning(ex, "Could not end the stale link session {Session}", session.Id);
            }
        }

        return ended;
    }

    /// <summary>
    /// The name a viewer's session shows under here: their client and their user on the server holding
    /// the link, as in <c>Jellyfin Web (Joe)</c>. A linking server that sends no user name, as releases
    /// before 12.2.11.4 do, is named instead.
    /// </summary>
    public static string DeviceName(SourceServer peer, PeerPlaybackReport report)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(report);
        var client = Clean(report.ClientName);
        var user = Clean(report.UserName);
        return (client.Length, user.Length) switch
        {
            ( > 0, > 0) => $"{client} ({user})",
            (_, > 0) => $"{peer.DisplayName} ({user})",
            ( > 0, _) => $"{peer.DisplayName} (Server Sync, {client})",
            _ => $"{peer.DisplayName} (Server Sync)"
        };
    }

    // Names come from another server, so they are held to a length and kept to one plain line.
    private static string Clean(string? name)
        => new string((name ?? string.Empty).Where(c => !char.IsControl(c)).Take(64).ToArray()).Trim();
}
