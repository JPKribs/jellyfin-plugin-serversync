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
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// The operator's view of the hint queues on this server: what is owed to each peer, what peers have
/// sent that is not yet applied, and which peers are paused and why. Also lets the operator discard a
/// row that will never go through and run a delivery or apply pass without waiting.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("ServerSync/Hints")]
[Produces(MediaTypeNames.Application.Json)]
public class HintsController : ControllerBase
{
    // The queue view polls every few seconds, so each lane is capped and the counts come from the table.
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
    /// <param name="outbound">The outbound store.</param>
    /// <param name="inbound">The inbound store.</param>
    /// <param name="outboundWorker">The delivery worker.</param>
    /// <param name="inboundWorker">The apply worker.</param>
    /// <param name="observer">The local change observer.</param>
    /// <param name="configManager">Plugin configuration.</param>
    /// <param name="clientFactory">Client factory for peers.</param>
    /// <param name="applicationHost">The server host, for this server's id.</param>
    /// <param name="versions">The version store, for the detail modals.</param>
    /// <param name="libraryManager">Library manager, to name the changes still gathering.</param>
    /// <param name="userManager">User manager, to name the changes still gathering.</param>
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
    /// Checks a server entry against the mode it is about to be saved with. Pull works against any
    /// server and only warns when changes will not also arrive as they happen, which needs Server Sync
    /// there listing this server as Push or Sync. Push needs the server to run Server Sync and list this
    /// server as Pull or Sync, since it must pull what this server announces. Sync needs the server to
    /// list this server as Sync too. A link that would only fail in the queue is caught at setup.
    /// </summary>
    /// <param name="request">The entry's URL and key, as the connection test takes them.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was learned.</returns>
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
            result.Message = "Enter the server URL and API key first.";
            return Ok(result);
        }

        var apiKey = _configManager.ResolveRequestApiKey(request.ApiKey, request.ServerKey);

        SourceServerClient client;
        try
        {
            client = _clientFactory.Create(new SourceServer { Url = request.ServerUrl.Trim(), ApiKey = apiKey, AllowPrivateNetwork = request.AllowPrivateNetwork });
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
            if (connection.IsAdministrator == false)
            {
                result.Severity = mode == ServerMode.Pull ? "warn" : "error";
                result.Message = mode == ServerMode.Pull
                    ? "Connected with a standard user's key. This server will pull what that user can see on a schedule, but changes will not arrive as they happen, since Server Sync's endpoints there need an administrator's key."
                    : "Connected with a standard user's key. Push and Sync need an administrator's key or sign in, because Server Sync's endpoints there require elevation. Pull still works for what that user can see.";
                return Ok(result);
            }

            PeerCapabilities? capabilities;
            try
            {
                capabilities = await client.GetPeerCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                result.Message = "Connected, but its Server Sync refused this key. Push and Sync need an administrator's key.";
                return Ok(result);
            }
            catch (Exception ex)
            {
                result.Message = $"Connected, but its Server Sync could not be reached: {ex.Message}";
                return Ok(result);
            }

            if (capabilities is null)
            {
                result.Severity = mode == ServerMode.Pull ? "warn" : "error";
                result.Message = mode == ServerMode.Pull
                    ? "Connected. Server Sync is not installed there, so this server will pull on a schedule but changes will not arrive as they happen."
                    : "Connected. Server Sync is not installed there, so it cannot take changes from this server. Install Server Sync there, or use Pull.";
                return Ok(result);
            }

            result.HasPlugin = true;
            result.PluginVersion = capabilities.PluginVersion;
            result.SupportsHints = capabilities.Features.Contains(HintProtocol.HintFeature);
            if (!result.SupportsHints)
            {
                result.Severity = mode == ServerMode.Pull ? "warn" : "error";
                result.Message = mode == ServerMode.Pull
                    ? $"Connected. Server Sync {capabilities.PluginVersion} there predates live changes, so this server will pull on a schedule only. Update it there for changes as they happen."
                    : $"Connected. Server Sync {capabilities.PluginVersion} there predates live changes. Update it before using Push or Sync.";
                return Ok(result);
            }

            // Conflicts are decided on the two clocks, so a peer whose clock is off is worth a sentence.
            var skew = capabilities.ServerTime.HasValue ? (DateTime.UtcNow - capabilities.ServerTime.Value.ToUniversalTime()).Duration() : TimeSpan.Zero;
            result.ClockSkewSeconds = (int)Math.Round(skew.TotalSeconds);
            var skewNote = skew > HintProtocol.ClockSkewWarning
                ? $" Its clock is about {FormatSkew(skew)} off from this server's, so edits made on both within that window may be settled the wrong way round. Put both servers on NTP."
                : string.Empty;

            result.Accepts = capabilities.Accepts;
            var acceptsNote = capabilities.Accepts is null ? string.Empty
                : capabilities.Accepts.Count == 0 ? " Every module is off there, so it applies no changes from this server."
                : $" It applies {JoinWords(capabilities.Accepts.Select(DescribeKind).ToList())} from this server. Other kinds are off there.";

            var link = await client.GetPeerLinkAsync(_applicationHost.SystemId, cancellationToken).ConfigureAwait(false);
            result.ListsThisServer = link is { Listed: true, Enabled: true, PullsFromYou: true };
            result.PeerMode = link?.Mode;
            result.SendsToThisServer = link is { Enabled: true, SendsToYou: true };
            var version = capabilities.PluginVersion;
            var listing = link is { Listed: true }
                ? $"lists this server as {link.Mode}{(link.Enabled ? string.Empty : ", disabled")}"
                : "does not list this server";

            switch (mode)
            {
                case ServerMode.Pull:
                    if (result.SendsToThisServer)
                    {
                        result.Severity = "ok";
                        result.Message = $"Ready. Server Sync {version} there {listing}, so this server will pull on a schedule and changes will also arrive as they happen.";
                    }
                    else
                    {
                        result.Severity = "warn";
                        result.Message = $"Server Sync {version} there {listing}. This server will pull on a schedule, but changes will not arrive as they happen until that server lists this one as Push or Sync.";
                    }

                    break;

                case ServerMode.Push:
                    if (result.ListsThisServer)
                    {
                        result.Severity = "ok";
                        result.Message = $"Ready. Server Sync {version} there {listing}, so it will pull the changes this server announces.";
                    }
                    else
                    {
                        result.Severity = "error";
                        result.Message = $"Server Sync {version} there {listing}. Push needs that server to list this one as Pull or Sync, enabled, with a URL and key.";
                    }

                    break;

                default:
                    if (link is { Listed: true, Enabled: true } && string.Equals(link.Mode, "Sync", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Severity = "ok";
                        result.Message = $"Ready. Server Sync {version} there lists this server as Sync, so changes travel both ways as they happen.";
                    }
                    else
                    {
                        result.Severity = "error";
                        result.Message = $"Server Sync {version} there {listing}. Sync needs that server to list this one as Sync, enabled, with a URL and key.";
                    }

                    break;
            }

            if (mode != ServerMode.Pull || result.SendsToThisServer)
            {
                result.Message += acceptsNote;
            }

            if (skewNote.Length > 0)
            {
                result.Message += skewNote;
                if (result.Severity == "ok")
                {
                    result.Severity = "warn";
                }
            }

            return Ok(result);
        }
    }

    // Jellyfin's own title for a local item, and a second line: the episode under its series, the year
    // under a film, the user under a watch history change. Falls back to the file or the name.
    private static (string Title, string? Subtitle) DisplayOf(MediaBrowser.Controller.Entities.BaseItem? item, HintKind kind, string? userName)
    {
        if (item is null)
        {
            return (userName ?? string.Empty, null);
        }

        if (item is MediaBrowser.Controller.Entities.TV.Episode episode)
        {
            var code = episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue
                ? $"S{episode.ParentIndexNumber.Value:D2}E{episode.IndexNumber.Value:D2} · "
                : string.Empty;
            return (episode.SeriesName ?? item.Name, code + item.Name);
        }

        if (kind == HintKind.People)
        {
            return (item.Name, null);
        }

        // A title that already ends with its year, as test libraries and some folder names do, is not repeated.
        var year = item.ProductionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return (item.Name, year is not null && item.Name.EndsWith($"({year})", StringComparison.Ordinal) ? null : year);
    }

    private static string DescribeKind(string kind) => kind switch
    {
        "History" => "watch history",
        "Users" => "user settings",
        "Content" => "files",
        _ => kind.ToLowerInvariant()
    };

    // "a", "a and b", "a, b, and c".
    private static string JoinWords(List<string> words) => words.Count switch
    {
        0 => string.Empty,
        1 => words[0],
        2 => $"{words[0]} and {words[1]}",
        _ => string.Join(", ", words.Take(words.Count - 1)) + ", and " + words[^1]
    };

    private static string FormatSkew(TimeSpan skew)
        => skew.TotalMinutes >= 1 ? $"{Math.Round(skew.TotalMinutes)} minute(s)" : $"{Math.Round(skew.TotalSeconds)} second(s)";

    /// <summary>Returns both queues and the state of every peer.</summary>
    /// <returns>The queues.</returns>
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
            Inbound = _inbound.GetOldest(OverviewRows).Select(InboundHintDto.From).ToList(),
            InboundCount = _inbound.Count()
        };
        foreach (var (state, count) in _outbound.CountByState())
        {
            overview.OutboundCounts[state.ToString()] = count;
        }

        // What is still gathering, named, so the operator sees a change the moment it is noticed.
        foreach (var change in _observer.Gathering().Take(OverviewRows))
        {
            var item = change.ItemId == Guid.Empty ? null : _libraryManager.GetItemById(change.ItemId);
            var userName = change.UserId == Guid.Empty ? null : _userManager.GetUserById(change.UserId)?.Username;
            var name = change.Kind == HintKind.People ? item?.Name : userName;
            var (title, subtitle) = DisplayOf(item, change.Kind, name);
            overview.Gathering.Add(new GatheringDto
            {
                Kind = change.Kind,
                Change = HintActivityLog.Subject(change.Kind, item?.Path, name, change.ItemId == Guid.Empty ? change.UserId.ToString("N") : change.ItemId.ToString("N")),
                Title = title,
                Subtitle = subtitle,
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
    /// The version an object carries on this server, for the detail modals: which server last edited it
    /// and when, or nothing when no edit has been recorded since versions began.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <param name="localItemId">The local item id, for metadata, content, and history.</param>
    /// <param name="localUserId">The local user id, for history and users.</param>
    /// <param name="name">The person's name, for people.</param>
    /// <returns>The version, or no content.</returns>
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
            _ => null
        };
        if (key is null)
        {
            return BadRequest("The ids for that kind are missing");
        }

        var version = _versions.Get(kind, key);
        if (version is null)
        {
            return NoContent();
        }

        var config = _configManager.Configuration;
        var serverName = string.Equals(version.ServerId, _applicationHost.SystemId, StringComparison.OrdinalIgnoreCase)
            ? _applicationHost.FriendlyName
            : config.Servers.FirstOrDefault(s => string.Equals(s.ServerId, version.ServerId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? version.ServerId;
        return Ok(new ObjectVersionDto { ServerId = version.ServerId, ServerName = serverName, Timestamp = version.Timestamp, IsThisServer = string.Equals(version.ServerId, _applicationHost.SystemId, StringComparison.OrdinalIgnoreCase) });
    }

    /// <summary>Raises every gathered local change now, re-reads what each peer accepts, delivers, and applies, instead of waiting for the workers.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpPost("Run")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> Run(CancellationToken cancellationToken)
    {
        _observer.Flush(DateTime.MaxValue);
        await _outboundWorker.DeliverAsync(cancellationToken, refreshCapabilities: true).ConfigureAwait(false);
        await _inboundWorker.ApplyAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Discards one outbound row.</summary>
    /// <param name="id">The row id.</param>
    /// <returns>No content, or not found.</returns>
    [HttpDelete("Outbound/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult RemoveOutbound([FromRoute] long id) => _outbound.Delete(id) ? NoContent() : NotFound();

    /// <summary>Discards one inbound row.</summary>
    /// <param name="id">The row id.</param>
    /// <returns>No content, or not found.</returns>
    [HttpDelete("Inbound/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult RemoveInbound([FromRoute] long id) => _inbound.Delete(id) ? NoContent() : NotFound();
}

/// <summary>The version an object carries, as the detail modals show it.</summary>
public class ObjectVersionDto
{
    /// <summary>Gets or sets the id of the server where the edit was made.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets that server's display name, or this server's name.</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>Gets or sets when the edit was made, in UTC.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Gets or sets a value indicating whether the edit was made on this server.</summary>
    public bool IsThisServer { get; set; }
}

/// <summary>Both queues and every peer's delivery state.</summary>
public class HintsOverview
{
    /// <summary>Gets or sets how many local changes are still gathering before they become hints.</summary>
    public int Pending { get; set; }

    /// <summary>Gets or sets how many local changes since start matched no mapping on any Push or Sync server.</summary>
    public int Unmatched { get; set; }

    /// <summary>Gets or sets the last such change, described.</summary>
    public string? LastUnmatched { get; set; }

    /// <summary>Gets or sets the changes still gathering before they become hints, newest edit first.</summary>
    public List<GatheringDto> Gathering { get; set; } = new();

    /// <summary>Gets or sets the newest outbound rows, at most a few hundred.</summary>
    public List<OutboundHintDto> Outbound { get; set; } = new();

    /// <summary>Gets or sets how many outbound rows exist per state, by state name.</summary>
    public Dictionary<string, int> OutboundCounts { get; set; } = new();

    /// <summary>Gets or sets the oldest inbound rows, at most a few hundred.</summary>
    public List<InboundHintDto> Inbound { get; set; } = new();

    /// <summary>Gets or sets how many inbound rows exist.</summary>
    public int InboundCount { get; set; }

    /// <summary>Gets or sets the delivery state of every server this one sends to.</summary>
    public List<PeerHintStateDto> Peers { get; set; } = new();
}

/// <summary>One change still gathering, as shown to the operator.</summary>
public class GatheringDto
{
    /// <summary>Gets or sets the kind.</summary>
    public HintKind Kind { get; set; }

    /// <summary>Gets or sets what changed, named.</summary>
    public string Change { get; set; } = string.Empty;

    /// <summary>Gets or sets Jellyfin's title for the item, or the person or user.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the second line: the episode, the year, or null.</summary>
    public string? Subtitle { get; set; }

    /// <summary>Gets or sets the local item id, for its poster.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets the local user id, for the avatar on a watch history change.</summary>
    public string? UserId { get; set; }

    /// <summary>Gets or sets the local username.</summary>
    public string? UserName { get; set; }

    /// <summary>Gets or sets when it was last edited, in UTC.</summary>
    public DateTime EditedAt { get; set; }

    /// <summary>Gets or sets when it is sent unless edited again, in UTC.</summary>
    public DateTime DueAt { get; set; }

    /// <summary>Gets or sets a value indicating whether a hand made edit is among the gathered changes.</summary>
    public bool Recorded { get; set; } = true;
}

/// <summary>One outbound row as shown to the operator.</summary>
public class OutboundHintDto
{
    /// <summary>Gets or sets the row id.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets Jellyfin's title for the item, or the person or user.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the second line: the episode, the year, or null.</summary>
    public string? Subtitle { get; set; }

    /// <summary>Gets or sets the local item id, for its poster.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets the local user id, for the avatar on a watch history change.</summary>
    public string? UserId { get; set; }

    /// <summary>Gets or sets the hint id.</summary>
    public string HintId { get; set; } = string.Empty;

    /// <summary>Gets or sets the peer's entry key.</summary>
    public string PeerKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the peer's display name.</summary>
    public string PeerName { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind.</summary>
    public HintKind Kind { get; set; }

    /// <summary>Gets or sets this server's key for the object.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the item's local path.</summary>
    public string? ItemPath { get; set; }

    /// <summary>Gets or sets the local username.</summary>
    public string? UserName { get; set; }

    /// <summary>Gets or sets the state.</summary>
    public OutboundState State { get; set; }

    /// <summary>Gets or sets how many deliveries have been tried.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets or sets when the next delivery may be tried.</summary>
    public DateTime NextAttempt { get; set; }

    /// <summary>Gets or sets when the peer accepted the hint.</summary>
    public DateTime? SentAt { get; set; }

    /// <summary>Gets or sets the last error.</summary>
    public string? LastError { get; set; }

    /// <summary>Gets or sets when the row was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Gets or sets a value indicating whether the change was made by hand rather than by a provider.</summary>
    public bool Recorded { get; set; } = true;
}

/// <summary>Delivery state of one peer as shown to the operator.</summary>
public class PeerHintStateDto
{
    /// <summary>Gets or sets the entry key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets when delivery was last tried.</summary>
    public DateTime? LastAttempt { get; set; }

    /// <summary>Gets or sets until when the peer is paused, or null.</summary>
    public DateTime? PausedUntil { get; set; }

    /// <summary>Gets or sets why the last delivery did not go through.</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Gets or sets the kinds this server sends to the peer, which are the modules selected there, or
    /// null when the peer has not said yet, in which case everything is sent.
    /// </summary>
    public List<string>? Sends { get; set; }
}
