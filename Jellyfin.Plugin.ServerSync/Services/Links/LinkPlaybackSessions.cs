using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Shows playback of a link on this server, the source, as a session of its own. Each viewer on the
/// server holding the link is a device here under the client name Server Sync, so the dashboard lists
/// it, the activity log and playback plugins see it, and Jellyfin's own stop handling updates played
/// state and resume position for the user it streams as. Jellyfin stops playback that has not checked
/// in for five minutes, and the session's controller stops counting as active ten minutes after the
/// last report, so Jellyfin closes the session when the server holding the link goes away.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkPlaybackSessions
{
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly MediaBrowser.Controller.IServerApplicationHost _applicationHost;
    private readonly Microsoft.Extensions.Logging.ILogger<LinkSessionController> _controllerLogger;

    public LinkPlaybackSessions(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        MediaBrowser.Controller.IServerApplicationHost applicationHost,
        Microsoft.Extensions.Logging.ILogger<LinkSessionController> controllerLogger)
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
        if (!peer.IsEnabled || !peer.AllowLinking)
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

        var mediaSourceId = item.Id.ToString("N", System.Globalization.CultureInfo.InvariantCulture);
        var method = Enum.TryParse<PlayMethod>(report.PlayMethod, true, out var parsed) ? parsed : PlayMethod.DirectStream;
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
                await _sessionManager.OnPlaybackStopped(new PlaybackStopInfo
                {
                    ItemId = item.Id,
                    SessionId = session.Id,
                    MediaSourceId = mediaSourceId,
                    PositionTicks = report.PositionTicks,
                    PlaySessionId = report.PlaySessionId
                }).ConfigureAwait(false);
                await _sessionManager.ReportSessionEnded(session.Id).ConfigureAwait(false);
                break;
            default:
                return "unknown playback event";
        }

        return null;
    }

    private static string DeviceName(SourceServer peer, PeerPlaybackReport report)
        => string.IsNullOrWhiteSpace(report.ClientName)
            ? $"{peer.DisplayName} (Server Sync)"
            : $"{peer.DisplayName} (Server Sync, {new string(report.ClientName.Where(c => !char.IsControl(c)).Take(64).ToArray())})";
}
