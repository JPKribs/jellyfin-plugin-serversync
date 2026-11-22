using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Asks the source whether a user here may play one of this server's links before Jellyfin starts on
/// it, so a refusal shows when they press play. When the link's own server is down, full, slow, or
/// refuses, the other pull servers that map the same folder are asked, and the highest ranked that allows it is used.
/// </summary>
[PluginService(ServiceLifetime.Singleton, ServiceType = typeof(ILinkAccess))]
public sealed class LinkAccess : ILinkAccess
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<LinkAccess> _logger;
    private readonly LinkHealth _health;
    private readonly TimeSpan _hedge;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkAccess"/> class.
    /// </summary>
    /// <param name="configManager">Reads the plugin configuration.</param>
    /// <param name="clientFactory">Builds clients for other servers.</param>
    /// <param name="applicationHost">Names this server.</param>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="health">Remembers which servers did not answer.</param>
    /// <param name="grants">The plays in progress, or null to apply no limits of this server.</param>
    public LinkAccess(IPluginConfigurationManager configManager, ISourceServerClientFactory clientFactory, IServerApplicationHost applicationHost, ILogger<LinkAccess> logger, LinkHealth health, LinkGrants? grants = null)
        : this(configManager, clientFactory, applicationHost, logger, health, Hedge)
    {
        Grants = grants;
    }

    /// <summary>
    /// Gets or sets the plays in progress here, which say how many links this server is streaming
    /// from each server. With none given, this server's own limits are not applied.
    /// </summary>
    public LinkGrants? Grants { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkAccess"/> class.
    /// </summary>
    /// <param name="configManager">Reads the plugin configuration.</param>
    /// <param name="clientFactory">Builds clients for other servers.</param>
    /// <param name="applicationHost">Names this server.</param>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="health">Remembers which servers did not answer.</param>
    /// <param name="hedge">How long the link's own server has before others are asked.</param>
    public LinkAccess(IPluginConfigurationManager configManager, ISourceServerClientFactory clientFactory, IServerApplicationHost applicationHost, ILogger<LinkAccess> logger, LinkHealth health, TimeSpan hedge)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(applicationHost);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(health);
        _configManager = configManager;
        _clientFactory = clientFactory;
        _applicationHost = applicationHost;
        _logger = logger;
        _health = health;
        _hedge = hedge;
    }

    /// <summary>
    /// How long the link's own server has to answer before the others are asked alongside it.
    /// </summary>
    public static TimeSpan Hedge { get; } = TimeSpan.FromSeconds(2);

    /// <summary>The source user this server maps a local user to on that server's entry, or null.</summary>
    /// <param name="server">The server entry whose user mappings are read.</param>
    /// <param name="localUserId">The local user to map, or null.</param>
    /// <returns>The source user's id, or null.</returns>
    public static string? SourceUserFor(SourceServer server, Guid? localUserId)
    {
        ArgumentNullException.ThrowIfNull(server);
        return localUserId is { } user
            ? server.UserMappings?.FirstOrDefault(m => m.IsEnabled && Guid.TryParse(m.LocalUserId, out var id) && id == user)?.SourceUserId
            : null;
    }

    /// <summary>The configured entry a link streams from, when it is still configured and on.</summary>
    /// <param name="link">The link whose server entry is looked up.</param>
    /// <returns>The server entry, or null.</returns>
    public SourceServer? ServerFor(LinkEntry link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var server = _configManager.Configuration.FindServer(link.ServerKey);
        return server is { IsEnabled: true, IsConfigured: true } ? server : null;
    }

    /// <inheritdoc />
    public Task<LinkChoice> ChooseAsync(LinkEntry link, Guid? userId, IReadOnlyCollection<string>? skip, CancellationToken cancellationToken)
        => ChooseRouteAsync(link, userId, false, skip, cancellationToken);

    /// <inheritdoc />
    public Task<LinkChoice> ChooseForDownloadAsync(LinkEntry link, Guid userId, IReadOnlyCollection<string>? skip, CancellationToken cancellationToken)
        => ChooseRouteAsync(link, userId, true, skip, cancellationToken);

    /// <summary>Asks the link's own server, then the others that hold the same file, until one allows the play or download.</summary>
    private async Task<LinkChoice> ChooseRouteAsync(LinkEntry link, Guid? userId, bool download, IReadOnlyCollection<string>? skip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        var config = _configManager.Configuration;
        string? firstRefusal = null;
        var primary = ServerFor(link);
        var primaryPassedOver = false;
        Task<LinkStreamCheckResponse>? primaryAsk = null;
        LinkStreamCheckResponse? busyPrimary = null;

        var streaming = Grants?.Streaming(null, link.LinkId, userId) ?? 0;

        // This server's own limit on links streamed at once comes first.
        if (config.LinkMaxStreamsTotal > 0 && streaming >= config.LinkMaxStreamsTotal)
        {
            return new LinkChoice(null, $"this server is already streaming {streaming} link(s), the most it streams at once");
        }

        if (primary is null)
        {
            firstRefusal = "the server this link streams from is no longer set up here";
        }
        else if (!Skipped(primary) && Full(primary))
        {
            firstRefusal = $"this server is already streaming {Taking(primary)} link(s) from {primary.DisplayName}, the most it takes from it";
        }
        else if (!Skipped(primary))
        {
            // A server that just failed to answer is asked last.
            if (_health.IsDown(primary.Key))
            {
                primaryPassedOver = true;
                firstRefusal = $"{primary.DisplayName} could not be reached";
            }
            else
            {
                primaryAsk = AskAsync(primary, link.LinkId, null, userId, download, cancellationToken);
                if (await Task.WhenAny(primaryAsk, Task.Delay(_hedge, cancellationToken)).ConfigureAwait(false) == primaryAsk)
                {
                    var answer = await primaryAsk.ConfigureAwait(false);
                    if (answer.Allowed && Idle(primary, answer))
                    {
                        return new LinkChoice(LinkFallback.PrimaryOf(link), null);
                    }

                    // An allowed but busy server yields to one with more room.
                    if (answer.Allowed)
                    {
                        busyPrimary = answer;
                    }
                    else
                    {
                        firstRefusal = answer.Reason;
                    }

                    primaryAsk = null;
                }
            }
        }

        var alsoTried = new List<string>();
        var alternates = LinkFallback.Alternates(config, link).Where(a => !Skipped(a.Server)).ToList();
        var asks = alternates.Select(a => _health.IsDown(a.Server.Key)
            ? Task.FromResult(new LinkStreamCheckResponse { Reason = $"{a.Server.DisplayName} could not be reached" })
            : Full(a.Server)
                ? Task.FromResult(new LinkStreamCheckResponse { Reason = $"this server is already streaming {Taking(a.Server)} link(s) from {a.Server.DisplayName}, the most it takes from it" })
                : AskAlternateAsync(a, link, userId, download, cancellationToken)).ToList();

        // The first idle server takes the play, else one with strictly more room.
        if (busyPrimary is not null)
        {
            var bestRoom = Room(primary!, busyPrimary);
            (SourceServer Server, LinkRoute Route)? roomier = null;
            for (var i = 0; i < alternates.Count; i++)
            {
                var answer = await asks[i].ConfigureAwait(false);
                if (!answer.Allowed)
                {
                    continue;
                }

                if (Idle(alternates[i].Server, answer))
                {
                    roomier = (alternates[i].Server, new LinkRoute(alternates[i].Server.Key, answer.ItemId!));
                    break;
                }

                if (Room(alternates[i].Server, answer) > bestRoom)
                {
                    bestRoom = Room(alternates[i].Server, answer);
                    roomier = (alternates[i].Server, new LinkRoute(alternates[i].Server.Key, answer.ItemId!));
                }
            }

            if (roomier is { } chosen)
            {
                _logger.LogDebug("Playing the link {Path} from '{Server}', which has more room than '{Primary}'", FormatUtilities.SanitizeForLog(link.LinkPath), chosen.Server.DisplayName, primary!.DisplayName);
                return new LinkChoice(chosen.Route, null);
            }

            return new LinkChoice(LinkFallback.PrimaryOf(link), null);
        }

        var standIn = FirstAllowedAsync();

        // A link's own server still answering wins if it allows the play first.
        if (primaryAsk is not null)
        {
            if (await Task.WhenAny(primaryAsk, standIn).ConfigureAwait(false) == primaryAsk || (await standIn.ConfigureAwait(false)) is null)
            {
                var answer = await primaryAsk.ConfigureAwait(false);
                if (answer.Allowed)
                {
                    return new LinkChoice(LinkFallback.PrimaryOf(link), null);
                }

                firstRefusal = answer.Reason;
            }
            else
            {
                firstRefusal = $"{primary!.DisplayName} was slow to answer";
            }
        }

        if (await standIn.ConfigureAwait(false) is { } found)
        {
            _logger.LogDebug(
                "Playing the link {Path} from '{Server}' instead: {Reason}",
                FormatUtilities.SanitizeForLog(link.LinkPath),
                found.Server.DisplayName,
                FormatUtilities.SanitizeForLog(firstRefusal ?? "the server it streamed from failed", 300));
            return new LinkChoice(found.Route, null);
        }

        for (var i = 0; i < alternates.Count; i++)
        {
            var answer = await asks[i].ConfigureAwait(false);
            _logger.LogDebug("'{Server}' cannot stand in for the link {Path}: {Reason}", FormatUtilities.SanitizeForLog(alternates[i].Server.DisplayName), FormatUtilities.SanitizeForLog(link.LinkPath), FormatUtilities.SanitizeForLog(answer.Reason, 300));
            alsoTried.Add(alternates[i].Server.DisplayName);
            firstRefusal ??= answer.Reason;
        }

        // Nobody else can play it, so the server passed over is asked after all.
        if (primaryPassedOver)
        {
            var answer = await AskAsync(primary!, link.LinkId, null, userId, download, cancellationToken).ConfigureAwait(false);
            if (answer.Allowed)
            {
                return new LinkChoice(LinkFallback.PrimaryOf(link), null);
            }

            firstRefusal = answer.Reason;
        }

        var refusal = firstRefusal ?? "the server this link streams from failed";
        return new LinkChoice(null, alsoTried.Count == 0 ? refusal : $"{refusal} (also tried {string.Join(", ", alsoTried)})");

        bool Skipped(SourceServer server) => skip?.Contains(server.Key, StringComparer.OrdinalIgnoreCase) == true;

        int Taking(SourceServer server) => Grants?.Streaming(server.Key, link.LinkId, userId) ?? 0;

        bool Full(SourceServer server) => server.LinkTakeMaxStreams > 0 && Taking(server) >= server.LinkTakeMaxStreams;

        bool Idle(SourceServer server, LinkStreamCheckResponse answer) => answer.StreamsInUse <= 0 && Taking(server) == 0;

        // Returns the streams free between the two servers, with no limit as the most.
        long Room(SourceServer server, LinkStreamCheckResponse answer)
        {
            var theirs = (answer.StreamLimit > 0 ? answer.StreamLimit : int.MaxValue) - (long)Math.Max(0, answer.StreamsInUse);
            var mine = (server.LinkTakeMaxStreams > 0 ? server.LinkTakeMaxStreams : int.MaxValue) - (long)Taking(server);
            return Math.Min(theirs, mine);
        }

        // Returns the first idle server that allows the play, else the roomiest.
        async Task<(SourceServer Server, LinkRoute Route)?> FirstAllowedAsync()
        {
            (SourceServer Server, LinkRoute Route)? busiest = null;
            var most = long.MinValue;
            for (var i = 0; i < alternates.Count; i++)
            {
                var answer = await asks[i].ConfigureAwait(false);
                if (!answer.Allowed)
                {
                    continue;
                }

                var found = (alternates[i].Server, new LinkRoute(alternates[i].Server.Key, answer.ItemId!));
                if (Idle(alternates[i].Server, answer))
                {
                    return found;
                }

                if (Room(alternates[i].Server, answer) > most)
                {
                    most = Room(alternates[i].Server, answer);
                    busiest = found;
                }
            }

            return busiest;
        }
    }

    /// <summary>Asks another server, and refuses it unless it names the item and holds a file of the same size.</summary>
    private async Task<LinkStreamCheckResponse> AskAlternateAsync(LinkAlternate alternate, LinkEntry link, Guid? userId, bool download, CancellationToken cancellationToken)
    {
        var answer = await AskAsync(alternate.Server, string.Empty, alternate.SourcePath, userId, download, cancellationToken).ConfigureAwait(false);
        if (answer.Allowed && string.IsNullOrEmpty(answer.ItemId))
        {
            return new LinkStreamCheckResponse { Reason = "it did not say which item holds the file" };
        }

        if (answer.Allowed && link.Size > 0 && answer.Size != link.Size)
        {
            return new LinkStreamCheckResponse { Reason = answer.Size > 0 ? "its copy of the file is a different size" : "it did not say how large its copy of the file is" };
        }

        return answer;
    }

    /// <summary>Asks one server whether the play or download is allowed, and records whether it answered.</summary>
    private async Task<LinkStreamCheckResponse> AskAsync(SourceServer server, string itemId, string? path, Guid? userId, bool download, CancellationToken cancellationToken)
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
                    SourceUserId = SourceUserFor(server, userId),
                    Download = download
                },
                deadline.Token).ConfigureAwait(false);
            _health.Answered(server.Key);

            // A source that did not check download permission cannot allow a download.
            if (download && answer.Allowed && !answer.DownloadAllowed)
            {
                return new LinkStreamCheckResponse { Reason = $"{server.DisplayName} needs a newer Server Sync to allow downloads" };
            }

            if (!answer.Allowed)
            {
                answer.Reason = FormatUtilities.SanitizeForLog(answer.Reason ?? "the source refused", 300);
            }

            return answer;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _health.Failed(server.Key);
            _logger.LogWarning(ex, "Could not ask '{Server}' whether a link may play", server.DisplayName);
            return new LinkStreamCheckResponse { Reason = $"{server.DisplayName} could not be reached" };
        }
    }
}
