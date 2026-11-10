using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>Where a link may play from, or why it may not play at all.</summary>
public sealed record LinkChoice(LinkRoute? Route, string? Refusal);

/// <summary>Asks the servers that hold a link's file whether a user here may play it.</summary>
public interface ILinkAccess
{
    /// <summary>
    /// Asks the link's own server, then the others that hold the same file, until one allows it.
    /// A null user is this server's own media probe.
    /// </summary>
    /// <param name="link">The link.</param>
    /// <param name="userId">The user here, or null for the probe.</param>
    /// <param name="skip">Server entries not to ask, such as one that just failed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The route to stream from, or why none would.</returns>
    Task<LinkChoice> ChooseAsync(LinkEntry link, Guid? userId, IReadOnlyCollection<string>? skip, CancellationToken cancellationToken);
}

/// <summary>
/// Asks the source whether a user here may play one of this server's links, before Jellyfin starts on
/// it, so a refusal shows when they press play rather than partway into the stream. When the link's own
/// server is down, full, or refuses, the other servers this one pulls from that map the same folder are
/// asked in priority order for the same file, by its path there and its size.
/// </summary>
[PluginService(ServiceLifetime.Singleton, ServiceType = typeof(ILinkAccess))]
public sealed class LinkAccess : ILinkAccess
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<LinkAccess> _logger;

    public LinkAccess(IPluginConfigurationManager configManager, ISourceServerClientFactory clientFactory, IServerApplicationHost applicationHost, ILogger<LinkAccess> logger)
    {
        _configManager = configManager;
        _clientFactory = clientFactory;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <summary>The source user this server maps a local user to on that server's entry, or null.</summary>
    public static string? SourceUserFor(SourceServer server, Guid? localUserId)
    {
        ArgumentNullException.ThrowIfNull(server);
        return localUserId is { } user
            ? server.UserMappings?.FirstOrDefault(m => m.IsEnabled && Guid.TryParse(m.LocalUserId, out var id) && id == user)?.SourceUserId
            : null;
    }

    /// <summary>The configured entry a link streams from, when it is still configured and on.</summary>
    public SourceServer? ServerFor(LinkEntry link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var server = _configManager.Configuration.FindServer(link.ServerKey);
        return server is { IsEnabled: true, IsConfigured: true } ? server : null;
    }

    /// <inheritdoc />
    public async Task<LinkChoice> ChooseAsync(LinkEntry link, Guid? userId, IReadOnlyCollection<string>? skip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        var config = _configManager.Configuration;
        string? firstRefusal = null;
        var alsoTried = new List<string>();
        var primary = ServerFor(link);
        if (primary is null)
        {
            firstRefusal = "the server this link streams from is no longer set up here";
        }
        else if (!Skipped(primary))
        {
            var answer = await AskAsync(primary, link.LinkId, null, userId, cancellationToken).ConfigureAwait(false);
            if (answer.Allowed)
            {
                return new LinkChoice(LinkFallback.PrimaryOf(link), null);
            }

            firstRefusal = answer.Reason;
        }

        foreach (var alternate in LinkFallback.Alternates(config, link))
        {
            if (Skipped(alternate.Server))
            {
                continue;
            }

            var answer = await AskAsync(alternate.Server, string.Empty, alternate.SourcePath, userId, cancellationToken).ConfigureAwait(false);
            if (answer.Allowed && string.IsNullOrEmpty(answer.ItemId))
            {
                answer = new LinkStreamCheckResponse { Reason = "it did not say which item holds the file" };
            }
            else if (answer.Allowed && link.Size > 0 && answer.Size > 0 && answer.Size != link.Size)
            {
                answer = new LinkStreamCheckResponse { Reason = "its copy of the file is a different size" };
            }

            if (answer.Allowed)
            {
                _logger.LogInformation(
                    "Playing the link {Path} from '{Server}' instead: {Reason}",
                    link.LinkPath,
                    alternate.Server.DisplayName,
                    firstRefusal ?? "the server it streamed from failed");
                return new LinkChoice(new LinkRoute(alternate.Server.Key, answer.ItemId!), null);
            }

            _logger.LogDebug("'{Server}' cannot stand in for the link {Path}: {Reason}", alternate.Server.DisplayName, link.LinkPath, answer.Reason);
            alsoTried.Add(alternate.Server.DisplayName);
            firstRefusal ??= answer.Reason;
        }

        var refusal = firstRefusal ?? "the server this link streams from failed";
        return new LinkChoice(null, alsoTried.Count == 0 ? refusal : $"{refusal} (also tried {string.Join(", ", alsoTried)})");

        bool Skipped(SourceServer server) => skip?.Contains(server.Key, StringComparer.OrdinalIgnoreCase) == true;
    }

    private async Task<LinkStreamCheckResponse> AskAsync(SourceServer server, string itemId, string? path, Guid? userId, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(CheckTimeout);
        try
        {
            using var client = _clientFactory.Create(server);
            var answer = await client.CheckLinkStreamAsync(
                new LinkStreamCheckRequest
                {
                    ServerId = _applicationHost.SystemId,
                    ItemId = itemId,
                    Path = path,
                    Probe = userId is null,
                    UserId = userId?.ToString("N", CultureInfo.InvariantCulture),
                    SourceUserId = SourceUserFor(server, userId)
                },
                deadline.Token).ConfigureAwait(false);
            if (!answer.Allowed)
            {
                answer.Reason = JPKribs.Jellyfin.Base.FormatUtilities.SanitizeForLog(answer.Reason ?? "the source refused", 300);
            }

            return answer;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Could not ask '{Server}' whether a link may play: {Reason}", server.DisplayName, ex.Message);
            return new LinkStreamCheckResponse { Reason = $"{server.DisplayName} could not be reached" };
        }
    }
}
