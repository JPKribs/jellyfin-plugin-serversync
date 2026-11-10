using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
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
/// A filter on Jellyfin's own routes, for this server's links only. When a user asks to play a link, it
/// asks the source whether they may, or another server that holds the same file when the source will
/// not, refuses at once if none will, and records the user and that server so the link route streams
/// as them from it. A film held in several versions is asked about version by version, and the versions
/// the source refuses, such as one above the user's bitrate limit, are left out of what the player is offered. It clears the link's loopback URL from every response, so clients play through this
/// server's own stream route and never see the link's secret, and stops universal audio from
/// redirecting a client to it. A player's direct play is answered here, streamed from the source for
/// that user, so two viewers of one link are each streamed, counted, and limited as themselves. Every other request passes through untouched, and a failure in the link
/// lookup never breaks playback.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkPlaybackFilter : IAsyncActionFilter, IAsyncResultFilter
{
    private const string UserIdClaim = "Jellyfin-UserId";

    // Where a user asks to play: these ask the source, then probe the link and decide how it plays.
    private static readonly string[] PlayControllers = { "MediaInfo", "UniversalAudio" };

    // Routes that hand out the item's file itself, which for a link is the .strm and its secret.
    private static readonly string[] FileActions = { "GetFile", "GetDownload" };

    // Where the versions the source refused are kept for one request, so its response leaves them out.
    private const string RefusedVersions = "ServerSync.Link.RefusedVersions";

    private readonly ILibraryManager _libraryManager;
    private readonly ILinkRegistry _links;
    private readonly LinkGrants _grants;
    private readonly ILinkAccess _access;
    private readonly ILogger<LinkPlaybackFilter> _logger;
    private readonly LinkStreamer? _streamer;
    private readonly IServiceProvider? _services;
    private readonly IPluginConfigurationManager? _configManager;

    public LinkPlaybackFilter(ILibraryManager libraryManager, ILinkRegistry links, LinkGrants grants, ILinkAccess access, ILogger<LinkPlaybackFilter> logger, LinkStreamer? streamer = null, IPluginConfigurationManager? configManager = null, IServiceProvider? services = null)
    {
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
        var links = FindLinks(context, out var controller, out var versions);
        if (links.Count > 0)
        {
            var userId = Guid.TryParse(context.HttpContext.User.FindFirst(UserIdClaim)?.Value, out var id) && id != Guid.Empty ? id : (Guid?)null;
            // Jellyfin serves a non-video .strm as a plain file, its text and secret included, so a link
            // reached through the audio routes is refused outright. Only video is linked, so this only
            // guards against a link made some other way.
            if (string.Equals(controller, "Library", StringComparison.Ordinal)
                || (controller is "Audio" or "UniversalAudio" && _libraryManager.GetItemById(LinkItemId(context)) is not MediaBrowser.Controller.Entities.Video))
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

                // A film held in several versions is asked about version by version, so a user refused
                // one, such as a file above their bitrate limit, is offered the versions they may play.
                var choices = await Task.WhenAll(links.Select(l => _access.ChooseAsync(l.Link, userId.Value, null, context.HttpContext.RequestAborted))).ConfigureAwait(false);
                var refused = new HashSet<Guid>();
                for (var i = 0; i < links.Count; i++)
                {
                    if (choices[i].Route is { } route)
                    {
                        _grants.Grant(links[i].Link.LinkId, userId.Value, route);
                    }
                    else
                    {
                        _logger.LogInformation("Refused to play the link {Path}: {Reason}", links[i].Link.LinkPath, choices[i].Refusal);
                        refused.Add(links[i].VersionId);
                    }
                }

                // Nothing is left to play only when every version is a link that was refused.
                if (refused.Count == versions)
                {
                    context.Result = Refused(choices[0].Refusal ?? "no server that holds the file allowed it");
                    return;
                }

                if (refused.Count > 0)
                {
                    context.HttpContext.Items[RefusedVersions] = refused;
                }

                // Set even when the client left it out: an argument at its default is not in the
                // dictionary, and its default is to redirect the client to the link's URL.
                if (string.Equals(controller, "UniversalAudio", StringComparison.Ordinal))
                {
                    context.ActionArguments["enableRedirection"] = false;
                }
            }
            else if (userId is not { } streaming || !_grants.Refresh(links[0].Link.LinkId, streaming))
            {
                // A stream nobody asked to play through PlaybackInfo would read the link as whoever
                // last did, so it is refused before Jellyfin opens the link.
                context.Result = Refused("press play again, so this server can ask the source");
                return;
            }
            else if (_streamer is not null && _configManager?.Configuration.StreamLinksDirectly != false && IsDirectPlay(context, controller))
            {
                // Direct play needs nothing from Jellyfin but the bytes, and here the user is known. So
                // the file is streamed from the source for exactly this user, rather than through the
                // link's own address, which cannot tell two viewers of one link apart.
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
        try
        {
            if (context.Result is ObjectResult { Value: { } value })
            {
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

    // The items a response carries.
    private static IEnumerable<BaseItemDto> Items(object value)
        => value switch
        {
            BaseItemDto item => new[] { item },
            QueryResult<BaseItemDto> page => page.Items ?? Enumerable.Empty<BaseItemDto>(),
            IEnumerable<BaseItemDto> items => items,
            _ => Enumerable.Empty<BaseItemDto>()
        };

    // Media sources in a response whose path is a link's URL. Only those are ever changed.
    private static IEnumerable<MediaSourceInfo> LinkSources(object value)
    {
        var sources = value is PlaybackInfoResponse playback
            ? playback.MediaSources
            : Items(value).Where(i => i?.MediaSources is not null).SelectMany(i => i.MediaSources);

        return sources?.Where(s => LinkFiles.PointsAtLinkRoute(s?.Path)).ToList() ?? Enumerable.Empty<MediaSourceInfo>();
    }

    // Jellyfin leaves trickplay out of an item whose file is remote, which a .strm is, so the images a
    // link took from its source are listed here, the way Jellyfin lists them for a file.
    private async Task ListTrickplayAsync(BaseItemDto item)
    {
        if (_services?.GetService<MediaBrowser.Controller.Trickplay.ITrickplayManager>() is not { } trickplay)
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

    // The actions that play, stream, or hand out a link. Everything else on these controllers, such as
    // merging versions, passes through.
    private static bool Watched(ControllerActionDescriptor descriptor)
        => descriptor.ControllerName switch
        {
            "MediaInfo" => descriptor.ActionName.Contains("PlaybackInfo", StringComparison.Ordinal),
            "UniversalAudio" or "DynamicHls" => true,
            // Where a player reads the stream. Only a user the source already allowed may, which keeps their grant alive.
            "Videos" or "Audio" => descriptor.ActionName.Contains("Stream", StringComparison.Ordinal),

            // Subtitles and fonts held inside the file are extracted by reading it through the link, so
            // they are held to the same rule as the stream.
            "Subtitle" => descriptor.ActionName is "GetSubtitle" or "GetSubtitleWithTicks" or "GetSubtitlePlaylist",
            "VideoAttachments" => true,
            "Library" => FileActions.Contains(descriptor.ActionName, StringComparer.Ordinal),
            _ => false
        };

    // A player's direct play: the video stream route asked for the file as it is.
    private static bool IsDirectPlay(FilterContext context, string? controller)
        => string.Equals(controller, "Videos", StringComparison.Ordinal)
            && bool.TryParse(context.HttpContext.Request.Query["static"].ToString(), out var asIs)
            && asIs;

    private static Guid LinkItemId(FilterContext context) => RouteItemId(context) ?? Guid.Empty;

    // Jellyfin names the item differently on some routes: routeItemId for subtitles, videoId for attachments.
    private static Guid? RouteItemId(FilterContext context)
    {
        foreach (var name in new[] { "itemId", "routeItemId", "videoId" })
        {
            if (Guid.TryParse(context.RouteData.Values[name]?.ToString(), out var id))
            {
                return id;
            }
        }

        return null;
    }

    private static ObjectResult Refused(string reason)
        => new($"This item streams from another server, which refused: {reason}") { StatusCode = StatusCodes.Status403Forbidden };

    // The version of the item a request names, when it names one: a query value, a route value, or
    // the posted playback request.
    private static Guid? NamedVersion(FilterContext context)
    {
        foreach (var name in new[] { "mediaSourceId", "routeMediaSourceId" })
        {
            if (Guid.TryParse(context.RouteData.Values[name]?.ToString(), out var routed))
            {
                return routed;
            }
        }

        if (Guid.TryParse(context.HttpContext.Request.Query["mediaSourceId"].ToString(), out var queried))
        {
            return queried;
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

    // The links a request touches, each with the version of the item it is. Asking to play a film held
    // in several versions touches every version's link, unless the request names one. A stream reads
    // the version it names, or the item's own. Also says how many versions were looked at, links or not.
    private List<(Guid VersionId, LinkEntry Link)> FindLinks(FilterContext context, out string? controller, out int versions)
    {
        controller = null;
        versions = 0;
        var found = new List<(Guid, LinkEntry)>();
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
            var all = new List<MediaBrowser.Controller.Entities.BaseItem> { item };
            if (item is MediaBrowser.Controller.Entities.Video video)
            {
                all.AddRange((_libraryManager.GetLocalAlternateVersionIds(video) ?? Enumerable.Empty<Guid>())
                    .Select(id => _libraryManager.GetItemById(id))
                    .Where(version => version is not null)!);
                all.AddRange(_libraryManager.GetLinkedAlternateVersions(video) ?? Enumerable.Empty<MediaBrowser.Controller.Entities.Video>());
            }

            var named = NamedVersion(context);
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
