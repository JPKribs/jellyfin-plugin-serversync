using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

using JPKribs.Jellyfin.Base;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// The operator's view of the Change Queue, with what is owed to each peer, what peers sent that is not yet applied, and which peers are paused.
/// It also lets the operator discard a row and run a delivery or apply pass at once.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Sync/Hints")]
[Produces(MediaTypeNames.Application.Json)]
public class HintsController : ControllerBase
{
    /// <summary>
    /// The most rows each lane of the overview returns, since the page polls every few seconds.
    /// </summary>
    private const int OverviewRows = 500;

    private readonly OutboundHintStore _outbound;
    private readonly InboundHintStore _inbound;
    private readonly OutboundHintWorker _outboundWorker;
    private readonly InboundHintWorker _inboundWorker;
    private readonly LocalChangeObserver _observer;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IServerApplicationHost _applicationHost;
    private readonly VersionStore _versions;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="HintsController"/> class.
    /// </summary>
    /// <param name="outbound">Holds the hints owed to peers.</param>
    /// <param name="inbound">Holds the hints peers sent that are not yet applied.</param>
    /// <param name="outboundWorker">Delivers hints and knows which peers are paused.</param>
    /// <param name="inboundWorker">Applies received hints.</param>
    /// <param name="observer">Holds the local changes still gathering.</param>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="clientFactory">Builds the client for another server.</param>
    /// <param name="applicationHost">Gives this server's ID and name.</param>
    /// <param name="versions">Reads the stamp an object carries.</param>
    /// <param name="libraryManager">Names the items of changes still gathering.</param>
    /// <param name="userManager">Names the users of changes still gathering.</param>
    public HintsController(
        OutboundHintStore outbound,
        InboundHintStore inbound,
        OutboundHintWorker outboundWorker,
        InboundHintWorker inboundWorker,
        LocalChangeObserver observer,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        IServerApplicationHost applicationHost,
        VersionStore versions,
        ILibraryManager libraryManager,
        IUserManager userManager)
    {
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(outboundWorker);
        ArgumentNullException.ThrowIfNull(inboundWorker);
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(applicationHost);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(userManager);

        _libraryManager = libraryManager;
        _userManager = userManager;
        _versions = versions;
        _outbound = outbound;
        _inbound = inbound;
        _outboundWorker = outboundWorker;
        _inboundWorker = inboundWorker;
        _observer = observer;
        _configManager = configManager;
        _clientFactory = clientFactory;
        _applicationHost = applicationHost;
    }

    /// <summary>
    /// Checks a server entry against the Direction it is about to be saved with, so a pairing that would only fail in the queue is caught at setup.
    /// Pull works against any server, Push needs the other server to list this one as Pull or Sync, and Sync needs it to list this one as Sync.
    /// </summary>
    /// <param name="request">The entry's Server URL, API Key, and Direction.</param>
    /// <param name="cancellationToken">Cancels the calls to the other server.</param>
    /// <returns>What was learned about the other server, with a verdict for the Direction.</returns>
    [HttpPost("CheckPeer")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<PeerCheckResult>> CheckPeer([FromBody] TestConnectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = new PeerCheckResult();
        var mode = string.Equals(request.Mode, "Sync", StringComparison.OrdinalIgnoreCase) ? ServerMode.Sync
            : string.Equals(request.Mode, "Push", StringComparison.OrdinalIgnoreCase) ? ServerMode.Push
            : ServerMode.Pull;
        if (string.IsNullOrWhiteSpace(request.ServerUrl) || string.IsNullOrWhiteSpace(request.ApiKey))
        {
            result.Message = "Enter the Server URL and API Key.";
            return Ok(result);
        }

        SourceServerClient client;
        try
        {
            var apiKey = _configManager.ResolveRequestApiKey(request.ApiKey, request.ServerKey, request.ServerUrl.Trim());
            client = _clientFactory.Create(new SourceServer { Key = request.ServerKey ?? SourceServer.NewKey(), Url = request.ServerUrl.Trim(), ApiKey = apiKey, AllowPrivateNetwork = request.AllowPrivateNetwork });
        }
        catch (ArgumentException ex)
        {
            result.Message = ex.Message;
            return Ok(result);
        }

        using (client)
        {
            var connection = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (!connection.Success)
            {
                result.Message = connection.Message ?? connection.ErrorMessage ?? "The server did not answer.";
                return Ok(result);
            }

            result.Reachable = true;
            result.ServerName = connection.ServerName;
            result.ServerId = connection.ServerId;
            PeerCapabilities? capabilities;
            try
            {
                capabilities = await client.GetPeerCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                var (_, why) = await client.CheckSyncAccessAsync(cancellationToken).ConfigureAwait(false);
                result.Message = $"Connected, but its Server Sync refused this call: {why}. Add this server or its user to the Whitelist there.";
                return Ok(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                result.Message = $"Connected, but its Server Sync could not be reached: {ex.Message}";
                return Ok(result);
            }

            if (capabilities is null)
            {
                return Limited(
                    result,
                    mode,
                    "Connected. Server Sync is not installed there, so this server will pull on a schedule but changes will not arrive as they happen.",
                    "Connected. Server Sync is not installed there, so it cannot take changes from this server. Install Server Sync there, or use Pull.");
            }

            result.HasPlugin = true;
            result.PluginVersion = capabilities.PluginVersion;
            if (!HintProtocol.Compatible(capabilities, _configManager.PluginVersion))
            {
                result.Severity = "error";
                result.Incompatible = true;
                result.Message = "Connected, but " + HintProtocol.ProtocolMismatch(capabilities, _configManager.PluginVersion) + ".";
                return Ok(result);
            }

            var skew = capabilities.ServerTime.HasValue ? (DateTime.UtcNow - JPKribs.Jellyfin.Base.UtcTime.AsUtc(capabilities.ServerTime.Value)).Duration() : TimeSpan.Zero;
            result.ClockSkewSeconds = (int)Math.Round(skew.TotalSeconds);
            var skewNote = skew > HintProtocol.ClockSkewWarning
                ? $" Its clock is about {FormatSkew(skew)} off from this server's, so edits made on both within that window may be settled the wrong way round. Put both servers on NTP."
                : string.Empty;

            result.Accepts = capabilities.Accepts;
            var acceptsNote = capabilities.Accepts.Count == 0 ? " Every module is off there, so it applies no changes from this server."
                : $" It applies {JoinWords(capabilities.Accepts.Select(DescribeKind).ToList())} from this server. Other kinds are off there.";

            PeerLinkResponse? link;
            try
            {
                link = await client.GetPeerLinkAsync(_applicationHost.SystemId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                result.Message = $"Connected, but Server Sync there did not say whether it lists this server: {ex.Message}";
                return Ok(result);
            }

            result.Paired = link is { Paired: true };
            result.PairingError = link?.PairingError;
            result.ListsThisServer = link is { Listed: true, Enabled: true, PullsFromYou: true };
            result.PeerMode = link?.Mode;
            result.SendsToThisServer = link is { Enabled: true, SendsToYou: true };
            result.PeerAccessLevel = link?.AccessLevel;
            result.PeerAccessUser = link?.AccessUser;
            result.PeerCanDownload = link?.CanDownload;
            (result.Severity, result.Message, result.Headline) = Verdict(mode, link, capabilities.PluginVersion, result);

            if (mode is not ServerMode.Pull || result.SendsToThisServer)
            {
                result.Message += acceptsNote;
            }

            AppendWarning(result, skewNote);

            if (link is { Listed: true })
            {
                result.Message += PeerAccessNote(link);
            }

            // A server that could not pair cannot prove itself, so its hints are refused.
            if (link is { Listed: true } && !result.Paired)
            {
                AppendWarning(result, $" It could not pair with this server: {link.PairingError ?? "no reason was given"}. Until it can, hints between the two are refused.");
            }

            result.Details = Details(mode, link, capabilities, result, skew);
            return Ok(result);
        }
    }

    /// <summary>
    /// Builds the verdict for the chosen Direction, given how the peer lists this server.
    /// </summary>
    private static (string Severity, string Message, string Headline) Verdict(ServerMode mode, PeerLinkResponse? link, string? version, PeerCheckResult result)
    {
        var listing = link is { Listed: true }
            ? $"lists this server as {link.Mode}{(link.Enabled ? string.Empty : ", disabled")}"
            : "does not list this server";
        return mode switch
        {
            ServerMode.Pull when result.SendsToThisServer => ("ok", $"Ready. Server Sync {version} there {listing}, so this server will pull on a schedule and changes will also arrive as they happen.", "Ready. This server pulls on a schedule, and changes also arrive as they happen."),
            ServerMode.Pull => ("warn", $"Server Sync {version} there {listing}. This server will pull on a schedule, but changes will not arrive as they happen until that server lists this one as Push or Sync.", "Ready on a schedule. Changes do not arrive as they happen."),
            ServerMode.Push when result.ListsThisServer => ("ok", $"Ready. Server Sync {version} there {listing}, so it will pull the changes this server announces.", "Ready. That server pulls the changes this server announces."),
            ServerMode.Push => ("error", $"Server Sync {version} there {listing}. Push needs that server to list this one as Pull or Sync, enabled, with a URL and key.", "Not ready. That server must list this one as Pull or Sync."),
            _ when link is { Listed: true, Enabled: true } && string.Equals(link.Mode, "Sync", StringComparison.OrdinalIgnoreCase)
                => ("ok", $"Ready. Server Sync {version} there lists this server as Sync, so changes travel both ways as they happen.", "Ready. Changes travel both ways as they happen."),
            _ => ("error", $"Server Sync {version} there {listing}. Sync needs that server to list this one as Sync, enabled, with a URL and key.", "Not ready. That server must list this one as Sync.")
        };
    }

    /// <summary>
    /// Builds the verdict as lines with badges, covering how the other server lists this one, which way changes travel, what it applies, and its access here.
    /// </summary>
    private static List<PeerCheckDetail> Details(ServerMode mode, PeerLinkResponse? link, PeerCapabilities capabilities, PeerCheckResult result, TimeSpan skew)
    {
        var rows = new List<PeerCheckDetail>();
        var listed = link is { Listed: true };
        var needsListing = mode is not ServerMode.Pull;
        var listedRight = mode switch
        {
            ServerMode.Push => result.ListsThisServer,
            ServerMode.Sync => link is { Listed: true, Enabled: true } && string.Equals(link.Mode, "Sync", StringComparison.OrdinalIgnoreCase),
            _ => result.SendsToThisServer
        };
        var wanted = mode switch { ServerMode.Push => "Pull or Sync", ServerMode.Sync => "Sync", _ => "Push or Sync" };
        rows.Add(new PeerCheckDetail
        {
            Title = "Its entry for this server",
            Text = !listed ? $"This server is not on its Servers tab. Add it there as {wanted}."
                : link!.Enabled == false ? "Its entry for this server is switched off."
                : listedRight ? $"Server Sync {capabilities.PluginVersion} there has this server on its Servers tab."
                : $"{mode} here needs it to be {wanted} there.",
            Badge = !listed ? "Not listed" : link!.Enabled ? link.Mode ?? "Listed" : "Disabled",
            State = listedRight ? "ok" : needsListing ? "no" : "limited"
        });

        if (mode is not ServerMode.Pull)
        {
            var pulls = result.ListsThisServer;
            rows.Add(new PeerCheckDetail
            {
                Title = "Changes made here",
                Text = pulls ? "It pulls what this server announces, as it happens." : "Nothing is sent until it lists this server as Pull or Sync.",
                Badge = pulls ? "Live" : "Not sent",
                State = pulls ? "ok" : "no"
            });
        }

        if (mode is not ServerMode.Push)
        {
            var live = result.SendsToThisServer;
            rows.Add(new PeerCheckDetail
            {
                Title = "Changes made there",
                Text = live ? "Pulled on a schedule, and announced to this server as they happen." : "Pulled on a schedule. They arrive as they happen once it lists this server as Push or Sync.",
                Badge = live ? "Live" : "Scheduled",
                State = live ? "ok" : mode is ServerMode.Sync ? "no" : "limited"
            });
        }

        if (mode is not ServerMode.Pull || result.SendsToThisServer)
        {
            var kinds = Enum.GetNames<HintKind>();
            var on = kinds.Where(k => capabilities.Accepts.Contains(k, StringComparer.OrdinalIgnoreCase)).Select(DescribeKind).ToList();
            var off = kinds.Where(k => !capabilities.Accepts.Contains(k, StringComparer.OrdinalIgnoreCase)).Select(DescribeKind).ToList();
            rows.Add(new PeerCheckDetail
            {
                Title = "What it applies from this server",
                Text = on.Count == 0 ? "Every module is off there, so it applies no changes from this server."
                    : off.Count == 0 ? Sentence(JoinWords(on)) + "."
                    : $"{Sentence(JoinWords(on))}. Off there: {JoinWords(off)}.",
                Badge = on.Count == kinds.Length ? "Everything" : on.Count == 0 ? "Nothing" : $"{on.Count} of {kinds.Length}",
                State = on.Count == kinds.Length ? "ok" : on.Count == 0 ? "no" : "limited"
            });
        }

        if (listed && !string.IsNullOrEmpty(link!.AccessLevel))
        {
            var standard = string.Equals(link.AccessLevel, SourceServer.StandardUserAccess, StringComparison.OrdinalIgnoreCase);
            var who = string.IsNullOrWhiteSpace(link.AccessUser) ? "a standard user" : link.AccessUser;
            rows.Add(new PeerCheckDetail
            {
                Title = "What it reads here",
                Text = standard
                    ? $"It signs in as {who}, so it reads only the libraries that user can see, and only that user's watch history, settings, and playlists."
                    : "Every library and every user.",
                Badge = standard ? "Standard user" : "Administrator",
                State = standard ? "limited" : "ok"
            });
            if (link.CanDownload == false)
            {
                rows.Add(new PeerCheckDetail
                {
                    Title = "File downloads from here",
                    Text = "The account it signs in with may not download media here, so no files are copied to it.",
                    Badge = "Blocked",
                    State = "no"
                });
            }
        }

        else if (listed)
        {
            rows.Add(new PeerCheckDetail
            {
                Title = "What it reads here",
                Text = "Not known yet. It has not tested its connection to this server. Run Test Connection on its entry for this server.",
                Badge = "Unknown",
                State = "none"
            });
        }

        if (listed)
        {
            rows.Add(new PeerCheckDetail
            {
                Title = "Pairing",
                Text = result.Paired ? "The two servers have proved themselves to each other."
                    : $"It could not pair with this server: {link!.PairingError ?? "no reason was given"}. Until it can, changes between the two are refused.",
                Badge = result.Paired ? "Paired" : "Not paired",
                State = result.Paired ? "ok" : "no"
            });
        }

        if (skew > HintProtocol.ClockSkewWarning)
        {
            rows.Add(new PeerCheckDetail
            {
                Title = "Clocks",
                Text = "Edits made on both servers within that window may be settled the wrong way round. Put both servers on NTP.",
                Badge = FormatSkew(skew) + " apart",
                State = "limited"
            });
        }

        return rows;
    }

    /// <summary>Capitalizes the first letter of a text.</summary>
    private static string Sentence(string words) => words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];

    /// <summary>Describes the access the peer's key holds on this server.</summary>
    private static string PeerAccessNote(PeerLinkResponse link)
    {
        if (string.Equals(link.AccessLevel, SourceServer.StandardUserAccess, StringComparison.OrdinalIgnoreCase))
        {
            var who = string.IsNullOrWhiteSpace(link.AccessUser) ? "a standard user" : "the standard user " + link.AccessUser;
            var files = link.CanDownload == false ? " That user may not download media here, so no files are copied to it." : string.Empty;
            return $" It pulls from this server as {who}, so it only reads the libraries that user can see here, and only that user's watch history, settings, and playlists.{files}";
        }

        if (string.Equals(link.AccessLevel, SourceServer.AdministratorAccess, StringComparison.OrdinalIgnoreCase))
        {
            var files = link.CanDownload == false ? " The account it signed in with may not download media here, so no files are copied to it." : string.Empty;
            return " It pulls from this server with administrator access." + files;
        }

        return string.Empty;
    }

    /// <summary>
    /// Adds a caveat to the message. A ready link with a caveat is shown as a warning.
    /// </summary>
    private static void AppendWarning(PeerCheckResult result, string note)
    {
        if (note.Length == 0)
        {
            return;
        }

        result.Message += note;
        if (result.Severity == "ok")
        {
            result.Severity = "warn";
        }
    }

    /// <summary>
    /// Builds the answer for a server without Server Sync, a warning for Pull and an error for Push and Sync.
    /// </summary>
    private static ActionResult<PeerCheckResult> Limited(PeerCheckResult result, ServerMode mode, string pullMessage, string otherMessage)
    {
        result.Severity = mode == ServerMode.Pull ? "warn" : "error";
        result.Message = mode == ServerMode.Pull ? pullMessage : otherMessage;
        return result;
    }

    /// <summary>
    /// Reads Jellyfin's title for a local item with a second line, such as the episode under its series or the year under a film.
    /// </summary>
    private static (string Title, string? Subtitle) DisplayOf(MediaBrowser.Controller.Entities.BaseItem? item, HintKind kind, string? userName)
    {
        if (item is null)
        {
            return (userName ?? string.Empty, null);
        }

        if (item is MediaBrowser.Controller.Entities.TV.Episode episode)
        {
            var code = ItemTitles.EpisodeCode(episode) is { } number ? number + " · " : string.Empty;
            return (episode.SeriesName ?? item.Name, code + item.Name);
        }

        if (item is MediaBrowser.Controller.Entities.TV.Season season && !string.IsNullOrEmpty(season.SeriesName))
        {
            return (season.SeriesName, item.Name);
        }

        if (kind is HintKind.People or HintKind.Playlists)
        {
            return (item.Name, null);
        }

        var year = item.ProductionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // A title that already ends with its year does not repeat it.
        return (item.Name, year is not null && item.Name.EndsWith($"({year})", StringComparison.Ordinal) ? null : year);
    }

    /// <summary>Names a kind of hint in the reader's words.</summary>
    private static string DescribeKind(string kind) => kind switch
    {
        "History" => "watch history",
        "Users" => "user settings",
        "Content" => "files",
        "Playlists" => "playlists",
        _ => kind.ToLowerInvariant()
    };

    /// <summary>
    /// Joins words as a list in prose, with commas and a final "and".
    /// </summary>
    private static string JoinWords(List<string> words) => words.Count switch
    {
        0 => string.Empty,
        1 => words[0],
        2 => $"{words[0]} and {words[1]}",
        _ => string.Join(", ", words.Take(words.Count - 1)) + ", and " + words[^1]
    };

    /// <summary>Formats a clock difference in minutes, or seconds when under a minute.</summary>
    private static string FormatSkew(TimeSpan skew)
        => skew.TotalMinutes >= 1 ? ActivityText.Count((int)Math.Round(skew.TotalMinutes), "minute") : ActivityText.Count((int)Math.Round(skew.TotalSeconds), "second");

    /// <summary>Returns both queues and the state of every peer.</summary>
    /// <returns>Both queues, the changes still gathering, and each peer's state.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<HintsOverview> Get()
    {
        var config = _configManager.Configuration;
        var overview = new HintsOverview
        {
            Pending = _observer.PendingCount,
            Unmatched = _observer.UnmatchedCount,
            LastUnmatched = _observer.LastUnmatched,
            Inbound = _inbound.GetRecent(OverviewRows).Select(InboundHintDto.From).ToList(),
            InboundCount = _inbound.Count(),
            InboundFailingCount = _inbound.CountFailing()
        };
        foreach (var (state, count) in _outbound.CountByState())
        {
            overview.OutboundCounts[state.ToString()] = count;
        }

        foreach (var change in _observer.Gathering().Take(OverviewRows))
        {
            var item = change.ItemId == Guid.Empty ? null : _libraryManager.GetItemById(change.ItemId);
            var userName = change.UserId == Guid.Empty ? null : _userManager.GetUserById(change.UserId)?.Username;
            var name = change.Kind is HintKind.People or HintKind.Playlists ? item?.Name : userName;
            var (title, subtitle) = DisplayOf(item, change.Kind, name);
            overview.Gathering.Add(new GatheringDto
            {
                Kind = change.Kind,
                Change = HintActivityLog.Subject(change.Kind, item?.Path, name, change.ItemId == Guid.Empty ? change.UserId.ToString("N") : change.ItemId.ToString("N")),
                Title = title,
                Subtitle = subtitle,
                ItemType = item?.GetType().Name,
                ItemId = change.ItemId == Guid.Empty ? null : change.ItemId.ToString("N"),
                UserId = change.UserId == Guid.Empty ? null : change.UserId.ToString("N"),
                UserName = userName,
                EditedAt = change.EditedAt,
                DueAt = change.Due,
                Recorded = change.Recorded
            });
        }

        foreach (var row in _outbound.GetRecent(OverviewRows))
        {
            var item = Guid.TryParse(row.ItemId, out var itemId) ? _libraryManager.GetItemById(itemId) : null;
            var (title, subtitle) = DisplayOf(item, row.Kind, row.UserName);
            overview.Outbound.Add(new OutboundHintDto
            {
                Title = title,
                Subtitle = subtitle,
                ItemType = (row.Kind == HintKind.History ? HintProtocol.SplitHistoryItemType(row.ItemType, out _) : row.ItemType) ?? item?.GetType().Name,
                ItemId = row.ItemId,
                UserId = row.UserId,
                Id = row.Id,
                HintId = row.HintId,
                PeerKey = row.PeerKey,
                PeerName = config.FindServer(row.PeerKey)?.DisplayName ?? row.PeerKey,
                Kind = row.Kind,
                Key = row.Key,
                ItemPath = row.ItemPath,
                UserName = row.UserName,
                State = row.State,
                Attempts = row.Attempts,
                NextAttempt = row.NextAttempt,
                SentAt = row.SentAt,
                LastError = row.LastError,
                CreatedAt = row.CreatedAt,
                Recorded = row.Recorded
            });
        }

        foreach (var peer in config.GetPushServers())
        {
            _outboundWorker.PeerStates.TryGetValue(peer.Key, out var state);
            overview.Peers.Add(new PeerHintStateDto
            {
                Key = peer.Key,
                Name = peer.DisplayName,
                LastAttempt = state?.LastAttempt,
                PausedUntil = state?.PausedUntil,
                Reason = state?.Reason,
                Sends = state?.Accepts?.Select(k => k.ToString()).OrderBy(k => k, StringComparer.Ordinal).ToList()
            });
        }

        return Ok(overview);
    }

    /// <summary>
    /// Reads the Stamp an object carries on this server, which names the server that last edited it and when.
    /// </summary>
    /// <param name="kind">The sync type the object belongs to.</param>
    /// <param name="localItemId">The item's ID on this server, for Metadata, Content, History, and Playlists.</param>
    /// <param name="localUserId">The user's ID on this server, for History and Users.</param>
    /// <param name="name">The person's name, for people.</param>
    /// <returns>The stamp, 204 when no edit was recorded, or 400 when the IDs for the kind are missing.</returns>
    [HttpGet("Version")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<ObjectVersionDto> GetVersion([FromQuery] HintKind kind, [FromQuery] string? localItemId = null, [FromQuery] string? localUserId = null, [FromQuery] string? name = null)
    {
        var hasItem = Guid.TryParse(localItemId, out var itemId);
        var hasUser = Guid.TryParse(localUserId, out var userId);
        string? key = kind switch
        {
            HintKind.History when hasItem && hasUser => HintProtocol.HistoryKey(userId, itemId),
            HintKind.Metadata or HintKind.Content when hasItem => HintProtocol.MetadataKey(itemId),
            HintKind.Users when hasUser => HintProtocol.UsersKey(userId),
            HintKind.People when !string.IsNullOrWhiteSpace(name) => HintProtocol.PeopleKey(name),
            HintKind.Playlists when hasItem && _libraryManager.GetItemById(itemId) is { } playlist => Services.Playlists.PlaylistMirror.RootKey(playlist),
            _ => null
        };
        if (key is null)
        {
            return BadRequest("The ids for that kind are missing.");
        }

        var version = _versions.Get(kind, key);
        if (version is null)
        {
            return NoContent();
        }

        var isThisServer = string.Equals(version.ServerId, _applicationHost.SystemId, StringComparison.OrdinalIgnoreCase);
        var serverName = isThisServer
            ? _applicationHost.FriendlyName
            : _configManager.Configuration.FindServerById(version.ServerId)?.DisplayName ?? version.ServerId;
        return Ok(new ObjectVersionDto { ServerId = version.ServerId, ServerName = serverName, Timestamp = version.Timestamp, IsThisServer = isThisServer });
    }

    /// <summary>Sends every gathered local change, reads again what each peer accepts, delivers, and applies, without waiting for the workers.</summary>
    /// <param name="cancellationToken">Cancels the delivery and the apply.</param>
    /// <returns>204 once both passes ran.</returns>
    [HttpPost("Run")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> Run(CancellationToken cancellationToken)
    {
        _observer.Flush(DateTime.MaxValue);
        await _outboundWorker.DeliverAsync(cancellationToken, refreshCapabilities: true).ConfigureAwait(false);
        await _inboundWorker.ApplyNowAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Clears the warning about local changes that matched no mapping.</summary>
    /// <returns>204 once the warning is cleared.</returns>
    [HttpDelete("Unmatched")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult DismissUnmatched()
    {
        _observer.DismissUnmatched();
        return NoContent();
    }

    /// <summary>
    /// Tries every failed row again at once with its attempts cleared, delivering outbound rows and applying inbound ones.
    /// </summary>
    /// <param name="cancellationToken">Cancels the delivery and the apply.</param>
    /// <returns>How many rows were put back, in all and each way.</returns>
    [HttpPost("Failed/Retry")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> RetryFailed(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var outbound = _outbound.RetryFailed(now);
        var inbound = _inbound.RetryFailing(now);
        if (outbound > 0)
        {
            _outboundWorker.LiftPauses();
            await _outboundWorker.DeliverAsync(cancellationToken, refreshCapabilities: true).ConfigureAwait(false);
        }

        if (inbound > 0)
        {
            await _inboundWorker.ApplyNowAsync(cancellationToken).ConfigureAwait(false);
        }

        return Ok(new { Retried = outbound + inbound, Outbound = outbound, Inbound = inbound });
    }

    /// <summary>
    /// Discards every failed row, outbound and inbound. The scheduled tasks still carry the changes.
    /// </summary>
    /// <returns>How many rows were removed, in all and each way.</returns>
    [HttpDelete("Failed")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult DismissFailed()
    {
        var outbound = _outbound.DeleteFailed();
        var inbound = _inboundWorker.DismissFailing();
        return Ok(new { Removed = outbound + inbound, Outbound = outbound, Inbound = inbound });
    }

    /// <summary>Discards one outbound row.</summary>
    /// <param name="id">The ID of the outbound row.</param>
    /// <returns>204, or 404 when there is no such row.</returns>
    [HttpDelete("Outbound/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult RemoveOutbound([FromRoute] long id) => _outbound.Delete(id) ? NoContent() : NotFound();

    /// <summary>Discards one inbound row.</summary>
    /// <param name="id">The ID of the inbound row.</param>
    /// <returns>204, or 404 when there is no such row.</returns>
    [HttpDelete("Inbound/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult RemoveInbound([FromRoute] long id) => _inboundWorker.Dismiss(id) ? NoContent() : NotFound();
}
