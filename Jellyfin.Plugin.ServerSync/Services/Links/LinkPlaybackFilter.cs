using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// A filter on Jellyfin's own routes, for this server's links only, that asks the source whether a user
/// may play a link, refuses at once when no server allows it, and records the user and server the link
/// route streams as. It also clears the link's loopback URL from every response so clients never see the
/// link's secret, and answers a player's direct play by streaming from the source for that user.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkPlaybackFilter : IAsyncActionFilter, IAsyncResultFilter
{
    private const string UserIdClaim = "Jellyfin-UserId";

    /// <summary>The controllers where a user asks to play, which ask the source before the link is probed.</summary>
    private static readonly string[] PlayControllers = { "MediaInfo", "UniversalAudio" };

    /// <summary>
    /// The controllers Jellyfin answers with no user, which players ask by address alone.
    /// A request with no user is let through on these while a viewer the source allowed is playing the link.
    /// </summary>
    private static readonly string[] SentWithoutAUser = { "Subtitle", "VideoAttachments" };

    /// <summary>The actions that hand out the item's file itself, which for a link is the .strm and its secret.</summary>
    private static readonly string[] FileActions = { "GetFile", "GetDownload" };

    /// <summary>The request item that keeps the versions the source refused, so the response leaves them out.</summary>
    private const string RefusedVersions = "ServerSync.Link.RefusedVersions";

    private readonly ILibraryManager _libraryManager;
    private readonly ILinkRegistry _links;
    private readonly LinkGrants _grants;
    private readonly ILinkAccess _access;
    private readonly ILogger<LinkPlaybackFilter> _logger;
    private readonly LinkStreamer? _streamer;
    private readonly IServiceProvider? _services;
    private readonly LinkCache? _cache;
    private readonly IPluginConfigurationManager? _configManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkPlaybackFilter"/> class.
    /// </summary>
    /// <param name="libraryManager">Finds the item and versions a request names.</param>
    /// <param name="links">Tells which paths are this server's links.</param>
    /// <param name="grants">Records the users the source allowed to play a link.</param>
    /// <param name="access">Picks the server a link streams from.</param>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="streamer">Streams a direct play for its user, or null to leave it to Jellyfin.</param>
    /// <param name="configManager">Reads whether links stream directly, or null to stream them directly.</param>
    /// <param name="services">Resolves the user and trickplay managers on demand, or null.</param>
    /// <param name="cache">The link cache a held file plays from, or null.</param>
    public LinkPlaybackFilter(ILibraryManager libraryManager, ILinkRegistry links, LinkGrants grants, ILinkAccess access, ILogger<LinkPlaybackFilter> logger, LinkStreamer? streamer = null, IPluginConfigurationManager? configManager = null, IServiceProvider? services = null, LinkCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(logger);
        _cache = cache;
        _services = services;
        _streamer = streamer;
        _configManager = configManager;
        _libraryManager = libraryManager;
        _links = links;
        _grants = grants;
        _access = access;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // A download of a link hands out its .strm, and so its secret.
        if (DownloadsALink(context))
        {
            context.Result = new ObjectResult("This item streams from another server and cannot be downloaded from here") { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        // While the plugin is held the link table cannot be read.
        if (Plugin.Held)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var links = FindLinks(context, out var controller, out var versions);
        if (links.Count > 0)
        {
            var userId = Guid.TryParse(context.HttpContext.User.FindFirst(UserIdClaim)?.Value, out var id) && id != Guid.Empty ? id : (Guid?)null;

            // Jellyfin serves a non-video .strm as a plain file, secret included.
            if (string.Equals(controller, "Library", StringComparison.Ordinal)
                || (controller is "Audio" or "UniversalAudio" && _libraryManager.GetItemById(LinkItemId(context)) is not Video))
            {
                context.Result = new ObjectResult("This item streams from another server and cannot be downloaded from here") { StatusCode = StatusCodes.Status403Forbidden };
                return;
            }

            if (PlayControllers.Contains(controller, StringComparer.Ordinal))
            {
                if (userId is null)
                {
                    context.Result = Refused("this server could not tell who is playing");
                    return;
                }

                // Each version is checked here first, since Jellyfin has not yet checked the user.
                var choices = await Task.WhenAll(links.Select(l => !MaySeeHere(l.VersionId, userId.Value)
                    ? Task.FromResult(new LinkChoice(null, "this server does not let you play this item"))
                    : MayPlayFromCache(l.VersionId, userId.Value, l.Link)
                        ? Task.FromResult(new LinkChoice(LinkFallback.PrimaryOf(l.Link), null))
                        : _access.ChooseAsync(l.Link, userId.Value, null, context.HttpContext.RequestAborted))).ConfigureAwait(false);
                var refused = new HashSet<Guid>();
                for (var i = 0; i < links.Count; i++)
                {
                    if (choices[i].Route is { } route)
                    {
                        _grants.Grant(links[i].Link.LinkId, userId.Value, route);
                    }
                    else
                    {
                        _logger.LogDebug("Refused to play the link {Path}: {Reason}", FormatUtilities.SanitizeForLog(links[i].Link.LinkPath), FormatUtilities.SanitizeForLog(choices[i].Refusal, 300));
                        refused.Add(links[i].VersionId);
                    }
                }

                // Nothing is left to play when every version is a refused link.
                if (refused.Count == versions)
                {
                    context.Result = Refused(choices[0].Refusal ?? "no server that holds the file allowed it");
                    return;
                }

                if (refused.Count > 0)
                {
                    context.HttpContext.Items[RefusedVersions] = refused;
                }

                // An argument left at its default would redirect the client to the link's URL.
                if (string.Equals(controller, "UniversalAudio", StringComparison.Ordinal))
                {
                    context.ActionArguments["enableRedirection"] = false;
                }
            }
            else if (userId is null && SentWithoutAUser.Contains(controller, StringComparer.Ordinal) && _grants.HasViewer(links[0].Link.LinkId))
            {
                // Players such as webOS ask for these with no token, as Jellyfin allows for any item. They are read as the viewer playing the link now.
            }
            else if (userId is not { } streaming || !_grants.Refresh(links[0].Link.LinkId, streaming))
            {
                // A stream nobody asked to play would read the link as whoever last did.
                _logger.LogDebug(
                    "Refused a {Controller} request for the link {Path}, since {Who} holds no permission to play it",
                    controller,
                    FormatUtilities.SanitizeForLog(links[0].Link.LinkPath),
                    userId is null ? "a request with no user" : "its user");
                context.Result = Refused("press play again, so this server can ask the source");
                return;
            }
            else if (_streamer is not null && _configManager?.Configuration.StreamLinksDirectly != false && IsDirectPlay(context, controller))
            {
                // Streaming here names the user, which the link's own address cannot.
                context.Result = new LinkStreamResult(_streamer, links[0].Link, new LinkGrant(streaming, false, _grants.RouteFor(links[0].Link.LinkId, streaming)));
                return;
            }
        }

        await next().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (Plugin.Held)
        {
            await next().ConfigureAwait(false);
            return;
        }

        // A failure here must never break one of Jellyfin's own responses.
        try
        {
            if (context.Result is ObjectResult { Value: { } value } result)
            {
                // A lazy list would be rebuilt when written, losing the changes made here.
                if (value is IEnumerable<BaseItemDto> lazyItems and not ICollection<BaseItemDto>)
                {
                    value = lazyItems.ToList();
                    result.Value = value;
                }
                else if (value is IEnumerable<RecommendationDto> lazyGroups and not ICollection<RecommendationDto>)
                {
                    value = lazyGroups.ToList();
                    result.Value = value;
                }

                // The versions the source refused this user are not offered to their player.
                if (value is PlaybackInfoResponse playback
                    && playback.MediaSources is not null
                    && context.HttpContext.Items.TryGetValue(RefusedVersions, out var held)
                    && held is HashSet<Guid> refused)
                {
                    playback.MediaSources = playback.MediaSources.Where(s => !Guid.TryParse(s?.Id, out var version) || !refused.Contains(version)).ToArray();
                }

                var linked = Items(value).Where(i => i?.MediaSources?.Any(s => LinkFiles.PointsAtLinkRoute(s?.Path)) == true).ToList();
                foreach (var source in LinkSources(value))
                {
                    source.IsRemote = false;
                    source.Protocol = MediaProtocol.File;
                    source.Path = null;
                }

                foreach (var item in linked)
                {
                    await ListTrickplayAsync(item).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear link addresses from a response, so it goes out unchanged");
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>The items a response carries.</summary>
    private static IEnumerable<BaseItemDto> Items(object value)
        => value switch
        {
            BaseItemDto item => new[] { item },
            QueryResult<BaseItemDto> page => page.Items ?? Enumerable.Empty<BaseItemDto>(),
            IEnumerable<BaseItemDto> items => items,
            IEnumerable<RecommendationDto> groups => groups.Where(g => g?.Items is not null).SelectMany(g => g.Items),
            AllThemeMediaResult themes => new[] { themes.ThemeVideosResult, themes.ThemeSongsResult, themes.SoundtrackSongsResult }
                .Where(t => t?.Items is not null).SelectMany(t => t.Items),
            _ => Enumerable.Empty<BaseItemDto>()
        };

    /// <summary>The media sources in a response whose path is a link's URL, the only ones ever changed.</summary>
    private static IEnumerable<MediaSourceInfo> LinkSources(object value)
    {
        var sources = value is PlaybackInfoResponse playback
            ? playback.MediaSources
            : Items(value).Where(i => i?.MediaSources is not null).SelectMany(i => i.MediaSources);

        return sources?.Where(s => LinkFiles.PointsAtLinkRoute(s?.Path)).ToList() ?? Enumerable.Empty<MediaSourceInfo>();
    }

    /// <summary>
    /// Lists the trickplay images a link took from its source, since Jellyfin leaves trickplay out of an
    /// item whose file is remote, which a .strm is.
    /// </summary>
    private async Task ListTrickplayAsync(BaseItemDto item)
    {
        if (_services?.GetService<ITrickplayManager>() is not { } trickplay)
        {
            return;
        }

        foreach (var source in item.MediaSources)
        {
            if (source?.Id is null || !Guid.TryParse(source.Id, out var version) || item.Trickplay?.ContainsKey(source.Id) == true)
            {
                continue;
            }

            var sets = await trickplay.GetTrickplayResolutions(version).ConfigureAwait(false);
            if (sets is { Count: > 0 })
            {
                item.Trickplay ??= new Dictionary<string, Dictionary<int, TrickplayInfoDto>>();
                item.Trickplay[source.Id] = sets.ToDictionary(set => set.Key, set => new TrickplayInfoDto(set.Value));
            }
        }
    }

    /// <summary>
    /// Whether an action plays, streams, or hands out a link. Everything else on these controllers, such
    /// as merging versions, passes through.
    /// </summary>
    private static bool Watched(ControllerActionDescriptor descriptor)
        => descriptor.ControllerName switch
        {
            "MediaInfo" => descriptor.ActionName.Contains("PlaybackInfo", StringComparison.Ordinal),
            "UniversalAudio" or "DynamicHls" => true,

            // Only a user the source already allowed may read the stream.
            "Videos" or "Audio" => descriptor.ActionName.Contains("Stream", StringComparison.Ordinal),

            // Subtitles and fonts inside the file are read through the link.
            "Subtitle" => descriptor.ActionName is "GetSubtitle" or "GetSubtitleWithTicks" or "GetSubtitlePlaylist",
            "VideoAttachments" => true,
            "Library" => FileActions.Contains(descriptor.ActionName, StringComparer.Ordinal),
            _ => false
        };

    /// <summary>
    /// Whether a user may play a cached link under this server's own rules, which Jellyfin has not yet
    /// applied when this filter runs. Anyone else is asked about at the source.
    /// </summary>
    private bool MayPlayFromCache(Guid versionId, Guid userId, LinkEntry link)
    {
        if (_cache?.Holds(link.LinkId) != true)
        {
            return false;
        }

        var user = _services?.GetService<IUserManager>()?.GetUserById(userId);
        return user is not null
            && !user.HasPermission(PermissionKind.IsDisabled)
            && user.HasPermission(PermissionKind.EnableMediaPlayback)
            && _libraryManager.GetItemById(versionId)?.IsVisible(user) == true;
    }

    /// <summary>
    /// Whether this server's own rules let a user play an item, being enabled, allowed to play media, and able
    /// to see it. With no user manager to ask, which is only so outside a running server, nothing is refused.
    /// </summary>
    private bool MaySeeHere(Guid versionId, Guid userId)
    {
        if (_services?.GetService<IUserManager>() is not { } users)
        {
            return true;
        }

        var user = users.GetUserById(userId);
        return user is not null
            && !user.HasPermission(PermissionKind.IsDisabled)
            && user.HasPermission(PermissionKind.EnableMediaPlayback)
            && _libraryManager.GetItemById(versionId)?.IsVisible(user) == true;
    }

    /// <summary>Whether a request is a player's direct play, the video stream route asked for the file as it is.</summary>
    private static bool IsDirectPlay(FilterContext context, string? controller)
        => string.Equals(controller, "Videos", StringComparison.Ordinal)
            && bool.TryParse(context.HttpContext.Request.Query["static"].ToString(), out var asIs)
            && asIs;

    /// <summary>The item a route names, or the empty id when it names none.</summary>
    private static Guid LinkItemId(FilterContext context) => RouteItemId(context) ?? Guid.Empty;

    /// <summary>
    /// Whether Jellyfin's download or file route reaches a .strm that points at the link route, judged from the
    /// file itself so it holds while the link table cannot be read. A file that cannot be read is refused,
    /// since it cannot be shown not to be a link.
    /// </summary>
    private bool DownloadsALink(ActionExecutingContext context)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor { ControllerName: "Library" } descriptor
            || !FileActions.Contains(descriptor.ActionName, StringComparer.Ordinal))
        {
            return false;
        }

        foreach (var id in new[] { RouteItemId(context), NamedVersion(context) })
        {
            if (id is not { } itemId || _libraryManager.GetItemById(itemId)?.Path is not { } path
                || !path.EndsWith(LinkPaths.Extension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (_links.Find(path) is not null || LinkFiles.PointsAtLinkRoute(LinkFiles.ReadUrl(path)))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not read {Path} to tell whether it is a link, so its download is refused", FormatUtilities.SanitizeForLog(path));
                return true;
            }
        }

        return false;
    }

    /// <summary>The item a route names, which Jellyfin calls routeItemId for subtitles and videoId for attachments.</summary>
    private static Guid? RouteItemId(FilterContext context)
    {
        // The subtitle route names its item routeItemId and lets a query itemId replace it.
        if (context.RouteData.Values.ContainsKey("routeItemId")
            && Guid.TryParse(context.HttpContext.Request.Query["itemId"].ToString(), out var queried))
        {
            return queried;
        }

        foreach (var name in new[] { "itemId", "routeItemId", "videoId" })
        {
            if (Guid.TryParse(context.RouteData.Values[name]?.ToString(), out var id))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>Builds the 403 answer that tells the player why the source refused.</summary>
    private static ObjectResult Refused(string reason)
        => new($"This item streams from another server, which refused: {reason}") { StatusCode = StatusCodes.Status403Forbidden };

    /// <summary>
    /// The version of the item a request names in a query value, a route value, or the posted playback request,
    /// read in the order Jellyfin reads them so the version checked is the version Jellyfin goes on to read.
    /// </summary>
    private static Guid? NamedVersion(FilterContext context)
    {
        var routeOnly = string.Equals(context.RouteData.Values["controller"]?.ToString(), "VideoAttachments", StringComparison.OrdinalIgnoreCase);

        // Jellyfin's attachment route reads only the route value, so a query value is ignored.
        if (!routeOnly && Guid.TryParse(context.HttpContext.Request.Query["mediaSourceId"].ToString(), out var queried))
        {
            return queried;
        }

        foreach (var name in new[] { "mediaSourceId", "routeMediaSourceId" })
        {
            if (Guid.TryParse(context.RouteData.Values[name]?.ToString(), out var routed))
            {
                return routed;
            }
        }

        if (context is ActionExecutingContext executing)
        {
            foreach (var argument in executing.ActionArguments.Values)
            {
                if (argument is not null and not string
                    && argument.GetType().GetProperty("MediaSourceId")?.GetValue(argument) is string posted
                    && Guid.TryParse(posted, out var named))
                {
                    return named;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the links a request touches, each with the version of the item it is, and counts the versions
    /// looked at. A request to play touches every version's link unless it names one, and a stream reads
    /// the version it names or the item's own.
    /// </summary>
    private List<(Guid VersionId, LinkEntry Link)> FindLinks(FilterContext context, out string? controller, out int versions)
    {
        controller = null;
        versions = 0;
        var found = new List<(Guid, LinkEntry)>();

        // A failed lookup must never break playback of anything else.
        try
        {
            if (context.ActionDescriptor is not ControllerActionDescriptor descriptor
                || !Watched(descriptor)
                || RouteItemId(context) is not { } itemId
                || _libraryManager.GetItemById(itemId) is not { } item)
            {
                return found;
            }

            controller = descriptor.ControllerName;
            var named = NamedVersion(context);
            var all = new List<BaseItem> { item };

            // A stream reads one version, so only a play request looks them all up.
            if (!PlayControllers.Contains(controller, StringComparer.Ordinal))
            {
                if (named is { } one && one != item.Id && _libraryManager.GetItemById(one) is { } version)
                {
                    all.Add(version);
                }
            }
            else if (item is Video video)
            {
                all.AddRange((_libraryManager.GetLocalAlternateVersionIds(video) ?? Enumerable.Empty<Guid>())
                    .Select(id => _libraryManager.GetItemById(id))
                    .Where(version => version is not null)!);
                all.AddRange(_libraryManager.GetLinkedAlternateVersions(video) ?? Enumerable.Empty<Video>());
            }

            var touched = named is { } wanted && all.Any(v => v.Id == wanted)
                ? all.Where(v => v.Id == wanted).Take(1).ToList()
                : PlayControllers.Contains(controller, StringComparer.Ordinal) ? all.DistinctBy(v => v.Id).ToList() : all.Take(1).ToList();
            versions = touched.Count;
            foreach (var version in touched)
            {
                if (version.Path is { } path && path.EndsWith(LinkPaths.Extension, StringComparison.OrdinalIgnoreCase) && _links.Find(path) is { } link)
                {
                    found.Add((version.Id, link));
                }
            }

            return found;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not tell whether a playback request is for a link, so it goes through unchanged");
            found.Clear();
            return found;
        }
    }
}
