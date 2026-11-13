using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Lets the source's dashboard control a viewer of one of its files on the server that holds the link.
/// Stop, pause, resume, and seek, and a message, go to that server, which passes them to the viewer's
/// client. Everything else is ignored.
/// </summary>
public sealed class LinkSessionController : ISessionController
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long after the last report the session still counts as active.</summary>
    public static readonly TimeSpan ActiveFor = TimeSpan.FromMinutes(10);

    private long _lastReportTicks = DateTime.UtcNow.Ticks;

    private readonly string _peerKey;
    private readonly string _sessionKey;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger _logger;

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
    public bool ActiveAt(DateTime now) => now - new DateTime(Interlocked.Read(ref _lastReportTicks), DateTimeKind.Utc) < ActiveFor;

    /// <summary>Records that the server holding the link reported on this session.</summary>
    public void Touch() => Touch(DateTime.UtcNow);

    /// <summary>Records that the server holding the link reported on this session at <paramref name="at"/>.</summary>
    public void Touch(DateTime at) => Interlocked.Exchange(ref _lastReportTicks, at.Ticks);

    /// <inheritdoc />
    public bool SupportsMediaControl => _configManager.Configuration.FindServer(_peerKey) is { IsEnabled: true, IsConfigured: true };

    /// <summary>The request to send for a session message, or null when it is not one this passes on.</summary>
    public static PeerControlRequest? RequestFor<T>(SessionMessageType name, T data)
    {
        // Jellyfin's own stop of a paused session names no user. That is this server's housekeeping and
        // must not stop someone watching on another server, so only a person's command is passed on.
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
            _logger.LogWarning("Could not pass a remote control command to '{Server}': {Reason}", server.DisplayName, ex.Message);
        }
    }
}
