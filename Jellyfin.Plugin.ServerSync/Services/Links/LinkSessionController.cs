using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Lets the source's dashboard control a viewer of one of its files on the linking server. Stop, pause,
/// resume, seek, and a message go to that server for the viewer's client, and everything else is ignored.
/// </summary>
public sealed class LinkSessionController : ISessionController
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long after the last report the session still counts as active.</summary>
    public static readonly TimeSpan ActiveFor = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long after the last report the session is removed. It is longer than the 16 minutes the
    /// dashboard lists a device for, so the dashboard sees the session idle before it is gone.
    /// </summary>
    public static readonly TimeSpan RemoveAfter = TimeSpan.FromMinutes(20);

    private long _lastReportTicks = DateTime.UtcNow.Ticks;

    private readonly string _peerKey;
    private readonly string _sessionKey;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkSessionController"/> class.
    /// </summary>
    /// <param name="peerKey">The key of the linking server's entry here.</param>
    /// <param name="sessionKey">The viewer's session key on the linking server.</param>
    /// <param name="configManager">Reads the plugin configuration.</param>
    /// <param name="clientFactory">Builds clients for other servers.</param>
    /// <param name="applicationHost">Names this server.</param>
    /// <param name="logger">Writes log entries.</param>
    public LinkSessionController(string peerKey, string sessionKey, IPluginConfigurationManager configManager, ISourceServerClientFactory clientFactory, IServerApplicationHost applicationHost, ILogger logger)
    {
        _peerKey = peerKey;
        _sessionKey = sessionKey;
        _configManager = configManager;
        _clientFactory = clientFactory;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsSessionActive => ActiveAt(DateTime.UtcNow);

    /// <summary>Whether the session still counts as active at <paramref name="now"/>.</summary>
    /// <param name="now">The time to check.</param>
    /// <returns><c>true</c> when the session is active.</returns>
    public bool ActiveAt(DateTime now) => now - new DateTime(Interlocked.Read(ref _lastReportTicks), DateTimeKind.Utc) < ActiveFor;

    /// <summary>Whether the session has gone unreported long enough to be removed at <paramref name="now"/>.</summary>
    /// <param name="now">The time to check.</param>
    /// <returns><c>true</c> when the session may be removed.</returns>
    public bool RemovableAt(DateTime now) => now - new DateTime(Interlocked.Read(ref _lastReportTicks), DateTimeKind.Utc) >= RemoveAfter;

    /// <summary>Records that the linking server reported on this session.</summary>
    public void Touch() => Touch(DateTime.UtcNow);

    /// <summary>Records that the linking server reported on this session at <paramref name="at"/>.</summary>
    /// <param name="at">When the report arrived.</param>
    public void Touch(DateTime at) => Interlocked.Exchange(ref _lastReportTicks, at.Ticks);

    /// <inheritdoc />
    public bool SupportsMediaControl => _configManager.Configuration.FindServer(_peerKey) is { IsEnabled: true, IsConfigured: true };

    /// <summary>The request to send for a session message, or null when it is not one this passes on.</summary>
    /// <typeparam name="T">The message's data type.</typeparam>
    /// <param name="name">The message type.</param>
    /// <param name="data">The message's data.</param>
    /// <returns>The request, or null.</returns>
    public static PeerControlRequest? RequestFor<T>(SessionMessageType name, T data)
    {
        // Jellyfin's own stop names no user and must not stop a viewer elsewhere.
        if (name == SessionMessageType.Playstate && data is PlaystateRequest playstate && !string.IsNullOrEmpty(playstate.ControllingUserId))
        {
            return new PeerControlRequest { Command = playstate.Command.ToString(), SeekPositionTicks = playstate.SeekPositionTicks };
        }

        if (name == SessionMessageType.GeneralCommand && data is GeneralCommand { Name: GeneralCommandType.DisplayMessage } command)
        {
            command.Arguments.TryGetValue("Header", out var header);
            command.Arguments.TryGetValue("Text", out var text);
            command.Arguments.TryGetValue("TimeoutMs", out var timeout);
            return new PeerControlRequest
            {
                Header = header,
                Text = text,
                TimeoutMs = long.TryParse(timeout, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? ms : null
            };
        }

        return null;
    }

    /// <inheritdoc />
    public async Task SendMessage<T>(SessionMessageType name, Guid messageId, T data, CancellationToken cancellationToken)
    {
        if (RequestFor(name, data) is not { } request
            || _configManager.Configuration.FindServer(_peerKey) is not { IsEnabled: true, IsConfigured: true } server)
        {
            return;
        }

        request.ServerId = _applicationHost.SystemId;
        request.SessionKey = _sessionKey;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(SendTimeout);
        try
        {
            using var client = _clientFactory.Create(server);
            await client.SendLinkControlAsync(request, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Could not pass a remote control command to '{Server}': {Reason}", server.DisplayName, FormatUtilities.SanitizeForLog(ex.Message, 300));
        }
    }
}
