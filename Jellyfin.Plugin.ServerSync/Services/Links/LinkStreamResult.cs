using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Answers a player's direct play or a download of a link by streaming the file from the link cache or
/// the source, for the user who asked. Jellyfin would otherwise read the link's own address without saying who for.
/// </summary>
public sealed class LinkStreamResult : IActionResult
{
    private readonly LinkStreamer _streamer;
    private readonly LinkEntry _link;
    private readonly LinkGrant _grant;
    private readonly string? _downloadName;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkStreamResult"/> class.
    /// </summary>
    /// <param name="streamer">Streams the file.</param>
    /// <param name="link">The link to stream.</param>
    /// <param name="grant">The grant that allows the stream.</param>
    /// <param name="downloadName">The file name a download is saved as, or null for a play.</param>
    public LinkStreamResult(LinkStreamer streamer, LinkEntry link, LinkGrant grant, string? downloadName = null)
    {
        ArgumentNullException.ThrowIfNull(streamer);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(grant);
        _streamer = streamer;
        _link = link;
        _grant = grant;
        _downloadName = downloadName;
    }

    /// <summary>Gets who the link is streamed for, and the server it is routed to.</summary>
    public LinkGrant Grant => _grant;

    /// <summary>Gets the file name a download is saved as, or null for a play.</summary>
    public string? DownloadName => _downloadName;

    /// <inheritdoc />
    public async Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var status = await _streamer.StreamAsync(context.HttpContext, _link, _grant, _link.CopiedPath, _downloadName).ConfigureAwait(false);
        if (status != LinkStreamer.Written && !context.HttpContext.Response.HasStarted)
        {
            context.HttpContext.Response.StatusCode = status;
        }
    }
}
