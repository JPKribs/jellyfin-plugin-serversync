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

namespace Jellyfin.Plugin.ServerSync.Api;

/// <summary>
/// The operator's view of the Change queue, with what is owed to each peer, what peers sent that is not yet applied, and which peers are paused.
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
        var lang = BaseTranslations.RequestLanguage(Request);
        var mode = string.Equals(request.Mode, "Sync", StringComparison.OrdinalIgnoreCase) ? ServerMode.Sync
            : string.Equals(request.Mode, "Push", StringComparison.OrdinalIgnoreCase) ? ServerMode.Push
            : ServerMode.Pull;
        if (string.IsNullOrWhiteSpace(request.ServerUrl) || string.IsNullOrWhiteSpace(request.ApiKey))
        {
            result.Message = Tr(lang, TranslationKeys.ErrorEnterUrlAndKey);
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
            var connection = await client.TestConnectionAsync(lang, cancellationToken).ConfigureAwait(false);
            if (!connection.Success)
            {
                result.Message = connection.Message ?? connection.ErrorMessage ?? Tr(lang, TranslationKeys.ErrorServerNoAnswer);
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
                var (_, why) = await client.CheckSyncAccessAsync(lang, cancellationToken).ConfigureAwait(false);
                result.Message = Tr(lang, TranslationKeys.PeerCheckRefused, why);
                return Ok(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                result.Message = Tr(lang, TranslationKeys.PeerCheckUnreachable, ex.Message);
                return Ok(result);
            }

            if (capabilities is null)
            {
                return Limited(
                    result,
                    mode,
                    Tr(lang, TranslationKeys.PeerCheckNoPluginPull),
                    Tr(lang, TranslationKeys.PeerCheckNoPluginOther));
            }

            result.HasPlugin = true;
            result.PluginVersion = capabilities.PluginVersion;
            if (!HintProtocol.Compatible(capabilities, _configManager.PluginVersion))
            {
                result.Severity = "error";
                result.Incompatible = true;
                result.Message = Tr(lang, TranslationKeys.PeerCheckIncompatible, HintProtocol.ProtocolMismatch(capabilities, _configManager.PluginVersion, lang));
                return Ok(result);
            }

            var skew = capabilities.ServerTime.HasValue ? (DateTime.UtcNow - JPKribs.Jellyfin.Base.UtcTime.AsUtc(capabilities.ServerTime.Value)).Duration() : TimeSpan.Zero;
            result.ClockSkewSeconds = (int)Math.Round(skew.TotalSeconds);
            var skewNote = skew > HintProtocol.ClockSkewWarning
                ? " " + Tr(lang, TranslationKeys.PeerCheckClockSkew, FormatSkew(skew, lang))
                : string.Empty;

            result.Accepts = capabilities.Accepts;
            var acceptsNote = " " + (capabilities.Accepts.Count == 0 ? Tr(lang, TranslationKeys.PeerCheckAcceptsNone)
                : Tr(lang, TranslationKeys.PeerCheckAcceptsSome, JoinWords(capabilities.Accepts.Select(k => DescribeKind(k, lang)).ToList(), lang)));

            PeerLinkResponse? link;
            try
            {
                link = await client.GetPeerLinkAsync(_applicationHost.SystemId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                result.Message = Tr(lang, TranslationKeys.PeerCheckLinkUnknown, ex.Message);
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
            (result.Severity, result.Message, result.Headline) = Verdict(mode, link, capabilities.PluginVersion, result, lang);

            if (mode is not ServerMode.Pull || result.SendsToThisServer)
            {
                result.Message += acceptsNote;
            }

            AppendWarning(result, skewNote);

            if (link is { Listed: true })
            {
                result.Message += PeerAccessNote(link, lang);
            }

            // A server that could not pair cannot prove itself, so its hints are refused.
            if (link is { Listed: true } && !result.Paired)
            {
                AppendWarning(result, " " + Tr(lang, TranslationKeys.PeerCheckNotPaired, link.PairingError ?? Tr(lang, TranslationKeys.PeerCheckNoReason)));
            }

            result.Details = Details(mode, link, capabilities, result, skew, lang);
            return Ok(result);
        }
    }

    /// <summary>
    /// Builds the verdict for the chosen Direction, given how the peer lists this server.
    /// </summary>
    private static (string Severity, string Message, string Headline) Verdict(ServerMode mode, PeerLinkResponse? link, string? version, PeerCheckResult result, string? lang)
    {
        var listing = link is { Listed: true }
            ? Tr(lang, link.Enabled ? TranslationKeys.PeerListsAs : TranslationKeys.PeerListsAsDisabled, ModeName(link.Mode, lang))
            : Tr(lang, TranslationKeys.PeerNotListed);
        return mode switch
        {
            ServerMode.Pull when result.SendsToThisServer => ("ok", Tr(lang, TranslationKeys.VerdictPullLive, version, listing), Tr(lang, TranslationKeys.HeadlinePullLive)),
            ServerMode.Pull => ("warn", Tr(lang, TranslationKeys.VerdictPullScheduled, version, listing), Tr(lang, TranslationKeys.HeadlinePullScheduled)),
            ServerMode.Push when result.ListsThisServer => ("ok", Tr(lang, TranslationKeys.VerdictPushReady, version, listing), Tr(lang, TranslationKeys.HeadlinePushReady)),
            ServerMode.Push => ("error", Tr(lang, TranslationKeys.VerdictPushNotReady, version, listing), Tr(lang, TranslationKeys.HeadlinePushNotReady)),
            _ when link is { Listed: true, Enabled: true } && string.Equals(link.Mode, "Sync", StringComparison.OrdinalIgnoreCase)
                => ("ok", Tr(lang, TranslationKeys.VerdictSyncReady, version), Tr(lang, TranslationKeys.HeadlineSyncReady)),
            _ => ("error", Tr(lang, TranslationKeys.VerdictSyncNotReady, version, listing), Tr(lang, TranslationKeys.HeadlineSyncNotReady))
        };
    }

    /// <summary>Names a Direction, such as Pull, in the reader's language. A name that is not a Direction is kept.</summary>
    private static string ModeName(string? mode, string? lang) => mode?.ToUpperInvariant() switch
    {
        "PULL" => Tr(lang, TranslationKeys.ModePull),
        "PUSH" => Tr(lang, TranslationKeys.ModePush),
        "SYNC" => Tr(lang, TranslationKeys.TabSync),
        _ => mode ?? string.Empty
    };

    /// <summary>Gets a string in a language and fills its placeholders.</summary>
    private static string Tr(string? lang, string key, params object?[] args) => BaseTranslations.Format(key, lang, args);

    /// <summary>
    /// Builds the verdict as lines with badges, covering how the other server lists this one, which way changes travel, what it applies, and its access here.
    /// </summary>
    private static List<PeerCheckDetail> Details(ServerMode mode, PeerLinkResponse? link, PeerCapabilities capabilities, PeerCheckResult result, TimeSpan skew, string? lang)
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
        var wanted = mode switch { ServerMode.Push => Tr(lang, TranslationKeys.ModePullOrSync), ServerMode.Sync => Tr(lang, TranslationKeys.TabSync), _ => Tr(lang, TranslationKeys.ModePushOrSync) };
        rows.Add(new PeerCheckDetail
        {
            Title = Tr(lang, TranslationKeys.DetailEntryTitle),
            Text = !listed ? Tr(lang, TranslationKeys.DetailNotOnServersTab, wanted)
                : link!.Enabled == false ? Tr(lang, TranslationKeys.DetailEntryOff)
                : listedRight ? Tr(lang, TranslationKeys.DetailListedRight, capabilities.PluginVersion)
                : Tr(lang, TranslationKeys.DetailNeedsMode, ModeName(mode.ToString(), lang), wanted),
            Badge = !listed ? Tr(lang, TranslationKeys.BadgeNotListed) : link!.Enabled ? (link.Mode is null ? Tr(lang, TranslationKeys.BadgeListed) : ModeName(link.Mode, lang)) : Tr(lang, TranslationKeys.Disabled),
            State = listedRight ? "ok" : needsListing ? "no" : "limited"
        });

        if (mode is not ServerMode.Pull)
        {
            var pulls = result.ListsThisServer;
            rows.Add(new PeerCheckDetail
            {
                Title = Tr(lang, TranslationKeys.DetailChangesHere),
                Text = pulls ? Tr(lang, TranslationKeys.DetailPullsAnnounced) : Tr(lang, TranslationKeys.DetailNothingSent),
                Badge = pulls ? Tr(lang, TranslationKeys.Live) : Tr(lang, TranslationKeys.BadgeNotSent),
                State = pulls ? "ok" : "no"
            });
        }

        if (mode is not ServerMode.Push)
        {
            var live = result.SendsToThisServer;
            rows.Add(new PeerCheckDetail
            {
                Title = Tr(lang, TranslationKeys.DetailChangesThere),
                Text = live ? Tr(lang, TranslationKeys.DetailPulledAndAnnounced) : Tr(lang, TranslationKeys.DetailPulledOnSchedule),
                Badge = live ? Tr(lang, TranslationKeys.Live) : Tr(lang, TranslationKeys.ColumnScheduled),
                State = live ? "ok" : mode is ServerMode.Sync ? "no" : "limited"
            });
        }

        if (mode is not ServerMode.Pull || result.SendsToThisServer)
        {
            var kinds = Enum.GetNames<HintKind>();
            var on = kinds.Where(k => capabilities.Accepts.Contains(k, StringComparer.OrdinalIgnoreCase)).Select(k => DescribeKind(k, lang)).ToList();
            var off = kinds.Where(k => !capabilities.Accepts.Contains(k, StringComparer.OrdinalIgnoreCase)).Select(k => DescribeKind(k, lang)).ToList();
            rows.Add(new PeerCheckDetail
            {
                Title = Tr(lang, TranslationKeys.DetailApplies),
                Text = on.Count == 0 ? Tr(lang, TranslationKeys.PeerCheckAcceptsNone)
                    : off.Count == 0 ? Sentence(JoinWords(on, lang)) + "."
                    : Tr(lang, TranslationKeys.DetailAppliesSome, Sentence(JoinWords(on, lang)), JoinWords(off, lang)),
                Badge = on.Count == kinds.Length ? Tr(lang, TranslationKeys.BadgeEverything) : on.Count == 0 ? Tr(lang, TranslationKeys.BadgeNothing) : Tr(lang, TranslationKeys.BadgeCountOf, on.Count, kinds.Length),
                State = on.Count == kinds.Length ? "ok" : on.Count == 0 ? "no" : "limited"
            });
        }

        if (listed && !string.IsNullOrEmpty(link!.AccessLevel))
        {
            var standard = string.Equals(link.AccessLevel, SourceServer.StandardUserAccess, StringComparison.OrdinalIgnoreCase);
            var who = string.IsNullOrWhiteSpace(link.AccessUser) ? Tr(lang, TranslationKeys.DetailStandardUser) : link.AccessUser;
            rows.Add(new PeerCheckDetail
            {
                Title = Tr(lang, TranslationKeys.DetailReads),
                Text = standard
                    ? Tr(lang, TranslationKeys.DetailSignsInAs, who)
                    : Tr(lang, TranslationKeys.DetailEveryLibrary),
                Badge = standard ? Tr(lang, TranslationKeys.BadgeStandardUser) : Tr(lang, TranslationKeys.BadgeAdministrator),
                State = standard ? "limited" : "ok"
            });
            if (link.CanDownload == false)
            {
                rows.Add(new PeerCheckDetail
                {
                    Title = Tr(lang, TranslationKeys.DetailDownloads),
                    Text = Tr(lang, TranslationKeys.DetailDownloadsBlocked),
                    Badge = Tr(lang, TranslationKeys.BadgeBlocked),
                    State = "no"
                });
            }
        }

        else if (listed)
        {
            rows.Add(new PeerCheckDetail
            {
                Title = Tr(lang, TranslationKeys.DetailReads),
                Text = Tr(lang, TranslationKeys.DetailReadsUnknown),
                Badge = Tr(lang, TranslationKeys.Unknown),
                State = "none"
            });
        }

        if (listed)
        {
            rows.Add(new PeerCheckDetail
            {
                Title = Tr(lang, TranslationKeys.DetailPairing),
                Text = result.Paired ? Tr(lang, TranslationKeys.DetailPaired)
                    : Tr(lang, TranslationKeys.DetailNotPaired, link!.PairingError ?? Tr(lang, TranslationKeys.PeerCheckNoReason)),
                Badge = result.Paired ? Tr(lang, TranslationKeys.BadgePaired) : Tr(lang, TranslationKeys.BadgeNotPaired),
                State = result.Paired ? "ok" : "no"
            });
        }

        if (skew > HintProtocol.ClockSkewWarning)
        {
            rows.Add(new PeerCheckDetail
            {
                Title = Tr(lang, TranslationKeys.DetailClocks),
                Text = Tr(lang, TranslationKeys.DetailClocksText),
                Badge = Tr(lang, TranslationKeys.BadgeApart, FormatSkew(skew, lang)),
                State = "limited"
            });
        }

        return rows;
    }

    /// <summary>Capitalizes the first letter of a text.</summary>
    private static string Sentence(string words) => words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];

    /// <summary>Describes the access the peer's key holds on this server.</summary>
    private static string PeerAccessNote(PeerLinkResponse link, string? lang)
    {
        if (string.Equals(link.AccessLevel, SourceServer.StandardUserAccess, StringComparison.OrdinalIgnoreCase))
        {
            var who = string.IsNullOrWhiteSpace(link.AccessUser) ? Tr(lang, TranslationKeys.DetailStandardUser) : Tr(lang, TranslationKeys.AccessTheStandardUser, link.AccessUser);
            var files = link.CanDownload == false ? " " + Tr(lang, TranslationKeys.AccessNoDownloadUser) : string.Empty;
            return " " + Tr(lang, TranslationKeys.AccessStandardUser, who) + files;
        }

        if (string.Equals(link.AccessLevel, SourceServer.AdministratorAccess, StringComparison.OrdinalIgnoreCase))
        {
            var files = link.CanDownload == false ? " " + Tr(lang, TranslationKeys.AccessNoDownloadAccount) : string.Empty;
            return " " + Tr(lang, TranslationKeys.AccessAdministrator) + files;
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
    private static string DescribeKind(string kind, string? lang) => kind switch
    {
        "History" => Tr(lang, TranslationKeys.KindWhatHistory),
        "Users" => Tr(lang, TranslationKeys.KindWhatUsers),
        "Content" => Tr(lang, TranslationKeys.KindFilesLower),
        "Playlists" => Tr(lang, TranslationKeys.KindPlaylistsLower),
        "Metadata" => Tr(lang, TranslationKeys.KindWhatMetadata),
        "People" => Tr(lang, TranslationKeys.KindPeopleLower),
        _ => kind.ToLowerInvariant()
    };

    /// <summary>
    /// Joins words as a list in prose, with commas and a final "and".
    /// </summary>
    private static string JoinWords(List<string> words, string? lang) => words.Count switch
    {
        0 => string.Empty,
        1 => words[0],
        2 => Tr(lang, TranslationKeys.ListTwo, words[0], words[1]),
        _ => Tr(lang, TranslationKeys.ListLast, string.Join(", ", words.Take(words.Count - 1)), words[^1])
    };

    /// <summary>Formats a clock difference in minutes, or seconds when under a minute.</summary>
    private static string FormatSkew(TimeSpan skew, string? lang)
    {
        if (skew.TotalMinutes >= 1)
        {
            var minutes = (int)Math.Round(skew.TotalMinutes);
            return Tr(lang, minutes == 1 ? TranslationKeys.CountMinuteOne : TranslationKeys.CountMinuteMany, minutes);
        }

        var seconds = (int)Math.Round(skew.TotalSeconds);
        return Tr(lang, seconds == 1 ? TranslationKeys.CountSecondOne : TranslationKeys.ValueSeconds, seconds);
    }

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
            LastUnmatched = _observer.DescribeLastUnmatched(BaseTranslations.RequestLanguage(Request)),
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
            // A file whose removal is held is gone from the library, so it is named from its path.
            var item = change.ItemId == Guid.Empty || change.RemovedPath is not null ? null : _libraryManager.GetItemById(change.ItemId);
            var userName = change.UserId == Guid.Empty ? null : _userManager.GetUserById(change.UserId)?.Username;
            var name = change.Kind is HintKind.People or HintKind.Playlists ? item?.Name : userName;
            var (title, subtitle) = change.RemovedPath is { } removedPath
                ? (System.IO.Path.GetFileNameWithoutExtension(removedPath), (string?)null)
                : DisplayOf(item, change.Kind, name);
            overview.Gathering.Add(new GatheringDto
            {
                Kind = change.Kind,
                Change = HintActivityLog.Subject(change.Kind, item?.Path ?? change.RemovedPath, name, change.ItemId == Guid.Empty ? change.UserId.ToString("N") : change.ItemId.ToString("N"), BaseTranslations.RequestLanguage(Request)),
                Title = title,
                Subtitle = subtitle,
                ItemType = change.RemovedPath is not null ? HintProtocol.ContentRemoved : item?.GetType().Name,
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
            return BadRequest(Tr(BaseTranslations.RequestLanguage(Request), TranslationKeys.ErrorKindIdsMissing));
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
