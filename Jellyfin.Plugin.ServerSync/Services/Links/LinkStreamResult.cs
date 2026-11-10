using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Answers a player's direct play of a link by streaming the file from the source, for the user who
/// asked. Jellyfin would otherwise read the link's own address to do the same, without saying who for.
/// </summary>
public sealed class LinkStreamResult : IActionResult
{
    private readonly LinkStreamer _streamer;
    private readonly LinkEntry _link;
    private readonly LinkGrant _grant;

    public LinkStreamResult(LinkStreamer streamer, LinkEntry link, LinkGrant grant)
    {
        _streamer = streamer;
        _link = link;
        _grant = grant;
    }

    /// <summary>Gets who the link is streamed for, and the server it is routed to.</summary>
    public LinkGrant Grant => _grant;

    /// <inheritdoc />
    public async Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var status = await _streamer.StreamAsync(context.HttpContext, _link, _grant, _link.CopiedPath).ConfigureAwait(false);
        if (status != LinkStreamer.Written && !context.HttpContext.Response.HasStarted)
        {
            context.HttpContext.Response.StatusCode = status;
        }
    }
}
