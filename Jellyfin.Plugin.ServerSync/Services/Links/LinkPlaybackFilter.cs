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
/// as them from it. It clears the link's loopback URL from every response, so clients play through this
/// server's own stream route and never see the link's secret, and stops universal audio from
/// redirecting a client to it. Every other request passes through untouched, and a failure in the link
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

    private readonly ILibraryManager _libraryManager;
    private readonly ILinkRegistry _links;
    private readonly LinkGrants _grants;
    private readonly ILinkAccess _access;
    private readonly ILogger<LinkPlaybackFilter> _logger;

    public LinkPlaybackFilter(ILibraryManager libraryManager, ILinkRegistry links, LinkGrants grants, ILinkAccess access, ILogger<LinkPlaybackFilter> logger)
    {
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
        var link = FindLink(context, out var controller);
        if (link is not null)
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

                var choice = await _access.ChooseAsync(link, userId.Value, null, context.HttpContext.RequestAborted).ConfigureAwait(false);
                if (choice.Route is not { } route)
                {
                    _logger.LogInformation("Refused to play the link {Path}: {Reason}", link.LinkPath, choice.Refusal);
                    context.Result = Refused(choice.Refusal ?? "no server that holds the file allowed it");
                    return;
                }

                _grants.Grant(link.LinkId, userId.Value, route);
                // Set even when the client left it out: an argument at its default is not in the
                // dictionary, and its default is to redirect the client to the link's URL.
                if (string.Equals(controller, "UniversalAudio", StringComparison.Ordinal))
                {
                    context.ActionArguments["enableRedirection"] = false;
                }
            }
            else if (userId is not { } streaming || !_grants.Refresh(link.LinkId, streaming))
            {
                // A stream nobody asked to play through PlaybackInfo would read the link as whoever
                // last did, so it is refused before Jellyfin opens the link.
                context.Result = Refused("press play again, so this server can ask the source");
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
                foreach (var source in LinkSources(value))
                {
                    source.IsRemote = false;
                    source.Protocol = MediaProtocol.File;
                    source.Path = null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear link addresses from a response, so it goes out unchanged");
        }

        await next().ConfigureAwait(false);
    }

    // Media sources in a response whose path is a link's URL. Only those are ever changed.
    private static IEnumerable<MediaSourceInfo> LinkSources(object value)
    {
        IEnumerable<MediaSourceInfo>? sources = value switch
        {
            PlaybackInfoResponse playback => playback.MediaSources,
            BaseItemDto item => item.MediaSources,
            QueryResult<BaseItemDto> page => page.Items?.Where(i => i?.MediaSources is not null).SelectMany(i => i.MediaSources),
            IEnumerable<BaseItemDto> items => items.Where(i => i?.MediaSources is not null).SelectMany(i => i.MediaSources),
            _ => null
        };

        return sources?.Where(s => LinkFiles.PointsAtLinkRoute(s?.Path)).ToList() ?? Enumerable.Empty<MediaSourceInfo>();
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

    private LinkEntry? FindLink(FilterContext context, out string? controller)
    {
        controller = null;
        try
        {
            if (context.ActionDescriptor is not ControllerActionDescriptor descriptor
                || !Watched(descriptor)
                || RouteItemId(context) is not { } itemId)
            {
                return null;
            }

            controller = descriptor.ControllerName;
            var path = _libraryManager.GetItemById(itemId)?.Path;
            return path is not null && path.EndsWith(LinkPaths.Extension, StringComparison.OrdinalIgnoreCase)
                ? _links.Find(path)
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not tell whether a playback request is for a link, so it goes through unchanged");
            return null;
        }
    }
}
