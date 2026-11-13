using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using JPKribs.Jellyfin.Base;
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
/// asked for the same file, by its path there and its size, and the highest in priority that allows it
/// is used. A server that did not answer a moment ago is passed over, and the others are asked together
/// once the link's own server is slow to answer, so a dead server never holds up play.
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

    public LinkAccess(IPluginConfigurationManager configManager, ISourceServerClientFactory clientFactory, IServerApplicationHost applicationHost, ILogger<LinkAccess> logger, LinkHealth health)
        : this(configManager, clientFactory, applicationHost, logger, health, Hedge)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LinkAccess"/> class, for tests.</summary>
    public LinkAccess(IPluginConfigurationManager configManager, ISourceServerClientFactory clientFactory, IServerApplicationHost applicationHost, ILogger<LinkAccess> logger, LinkHealth health, TimeSpan hedge)
    {
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
        var primary = ServerFor(link);
        var primaryPassedOver = false;
        Task<LinkStreamCheckResponse>? primaryAsk = null;
        if (primary is null)
        {
            firstRefusal = "the server this link streams from is no longer set up here";
        }
        else if (!Skipped(primary))
        {
            if (_health.IsDown(primary.Key))
            {
                // It did not answer a moment ago. It is asked last, when nobody else can play the file.
                primaryPassedOver = true;
                firstRefusal = $"{primary.DisplayName} could not be reached";
            }
            else
            {
                primaryAsk = AskAsync(primary, link.LinkId, null, userId, cancellationToken);
                if (await Task.WhenAny(primaryAsk, Task.Delay(_hedge, cancellationToken)).ConfigureAwait(false) == primaryAsk)
                {
                    var answer = await primaryAsk.ConfigureAwait(false);
                    if (answer.Allowed)
                    {
                        return new LinkChoice(LinkFallback.PrimaryOf(link), null);
                    }

                    firstRefusal = answer.Reason;
                    primaryAsk = null;
                }
            }
        }

        // The others are asked together, and answer in priority order.
        var alsoTried = new List<string>();
        var alternates = LinkFallback.Alternates(config, link).Where(a => !Skipped(a.Server)).ToList();
        var asks = alternates.Select(a => _health.IsDown(a.Server.Key)
            ? Task.FromResult(new LinkStreamCheckResponse { Reason = $"{a.Server.DisplayName} could not be reached" })
            : AskAlternateAsync(a, link, userId, cancellationToken)).ToList();
        var standIn = FirstAllowedAsync();

        // The link's own server is still thinking. It wins if it allows the play before another does.
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
            _logger.LogInformation(
                "Playing the link {Path} from '{Server}' instead: {Reason}",
                link.LinkPath,
                found.Server.DisplayName,
                firstRefusal ?? "the server it streamed from failed");
            return new LinkChoice(found.Route, null);
        }

        for (var i = 0; i < alternates.Count; i++)
        {
            var answer = await asks[i].ConfigureAwait(false);
            _logger.LogDebug("'{Server}' cannot stand in for the link {Path}: {Reason}", alternates[i].Server.DisplayName, link.LinkPath, answer.Reason);
            alsoTried.Add(alternates[i].Server.DisplayName);
            firstRefusal ??= answer.Reason;
        }

        // Nobody else can play it, so the server passed over is asked after all: it may be back.
        if (primaryPassedOver)
        {
            var answer = await AskAsync(primary!, link.LinkId, null, userId, cancellationToken).ConfigureAwait(false);
            if (answer.Allowed)
            {
                return new LinkChoice(LinkFallback.PrimaryOf(link), null);
            }

            firstRefusal = answer.Reason;
        }

        var refusal = firstRefusal ?? "the server this link streams from failed";
        return new LinkChoice(null, alsoTried.Count == 0 ? refusal : $"{refusal} (also tried {string.Join(", ", alsoTried)})");

        bool Skipped(SourceServer server) => skip?.Contains(server.Key, StringComparer.OrdinalIgnoreCase) == true;

        async Task<(SourceServer Server, LinkRoute Route)?> FirstAllowedAsync()
        {
            for (var i = 0; i < alternates.Count; i++)
            {
                var answer = await asks[i].ConfigureAwait(false);
                if (answer.Allowed)
                {
                    return (alternates[i].Server, new LinkRoute(alternates[i].Server.Key, answer.ItemId!));
                }
            }

            return null;
        }
    }

    // An alternate is only used when it names the item and holds the same file, told by its size.
    private async Task<LinkStreamCheckResponse> AskAlternateAsync(LinkAlternate alternate, LinkEntry link, Guid? userId, CancellationToken cancellationToken)
    {
        var answer = await AskAsync(alternate.Server, string.Empty, alternate.SourcePath, userId, cancellationToken).ConfigureAwait(false);
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
            _health.Answered(server.Key);
            if (!answer.Allowed)
            {
                answer.Reason = FormatUtilities.SanitizeForLog(answer.Reason ?? "the source refused", 300);
            }

            return answer;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _health.Failed(server.Key);
            _logger.LogWarning("Could not ask '{Server}' whether a link may play: {Reason}", server.DisplayName, ex.Message);
            return new LinkStreamCheckResponse { Reason = $"{server.DisplayName} could not be reached" };
        }
    }
}
