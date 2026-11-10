using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// The routes behind links: streaming this server's files to a server that holds links to them, and
/// listing this server's own links so a peer matches them by the file they stand in for.
/// </summary>
public partial class PeerController
{
    /// <summary>
    /// Streams one of this server's files to a server that holds a link to it, with range support. The
    /// caller must be paired and allowed to link here, and the user it streams for must pass this
    /// server's checks and limits.
    /// </summary>
    /// <param name="itemId">This server's id of the item.</param>
    /// <param name="serverId">The calling server's Jellyfin id.</param>
    /// <param name="userId">The user's id on the calling server, or empty for its media probe.</param>
    /// <param name="sourceUserId">The user here the calling server maps them to, if it does.</param>
    /// <param name="probe">Whether this is the calling server's media probe.</param>
    /// <param name="policy">The link checks.</param>
    /// <param name="sessions">The open link streams.</param>
    /// <returns>The file.</returns>
    [HttpGet("Stream/{itemId}")]
    [HttpHead("Stream/{itemId}")]
    [RequireCompatibleProtocol]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult Stream(
        [FromRoute] Guid itemId,
        [FromQuery] string? serverId,
        [FromQuery] string? userId,
        [FromQuery] string? sourceUserId,
        [FromQuery] bool probe,
        [FromServices] LinkPolicy policy,
        [FromServices] LinkSessions sessions,
        [FromServices] IUserManager userManager)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(sessions);
        var (peer, refusal) = Sender(serverId);
        if (peer is null)
        {
            return refusal!;
        }

        var verdict = HeldToScopedUser(policy.Check(peer, itemId, userId, sourceUserId, probe), userManager);
        if (!verdict.Allowed || verdict.Item is null)
        {
            _logger.LogWarning("Refused a link stream to '{Server}': {Reason}", peer.DisplayName, JPKribs.Jellyfin.Base.FormatUtilities.SanitizeForLog(verdict.Reason, 300));
            return StatusCode(StatusCodes.Status403Forbidden, verdict.Reason);
        }

        if (verdict.UserId is { } user)
        {
            // The limits again, now under one lock with opening the stream, so requests at the same moment cannot pass together.
            var handle = sessions.TryOpen(peer.Key, user, verdict.Item.Id, peer.LinkMaxStreams, peer.LinkMaxStreamsPerUser, policy.OwnPlayback(user, peer.LinkCountOwnSessions));
            if (handle is null)
            {
                return StatusCode(StatusCodes.Status403Forbidden, "the source's link stream limit is reached");
            }

            HttpContext.Response.RegisterForDispose(handle);
        }

        var mime = MediaBrowser.Model.Net.MimeTypes.GetMimeType(verdict.Item.Path);

        // A stream for a user is held to this server's speed for that server, when one is set. The
        // calling server's media probe is small and is never slowed.
        var speed = verdict.UserId is null ? 0 : peer.GetLinkStreamSpeedBytes(_configManager.Configuration.IsInBandwidthSchedule());
        if (speed <= 0)
        {
            return PhysicalFile(verdict.Item.Path, mime, enableRangeProcessing: true);
        }

        var file = new FileStream(verdict.Item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new FileStreamResult(new Utilities.ThrottledReadStream(file, speed), mime)
        {
            EnableRangeProcessing = true,
            LastModified = System.IO.File.GetLastWriteTimeUtc(verdict.Item.Path)
        };
    }

    /// <summary>
    /// Answers whether a user on the calling server may stream one of this server's files through a
    /// link, with the same checks as the stream, so a refusal shows before playback starts. The file is
    /// named by its id, or by its path here when the calling server's link is to another server and it
    /// is looking for the same file to fall back to. An allowed answer carries the item's id and the
    /// file's size, so the calling server can tell it is the same file.
    /// </summary>
    /// <param name="request">The item and user.</param>
    /// <param name="policy">The link checks.</param>
    /// <param name="userManager">The users here.</param>
    /// <param name="libraryManager">The library, to find a file by its path.</param>
    /// <returns>The answer.</returns>
    [HttpPost("Stream/Check")]
    [RequireCompatibleProtocol]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<LinkStreamCheckResponse> CheckStream(
        [FromBody] LinkStreamCheckRequest request,
        [FromServices] LinkPolicy policy,
        [FromServices] IUserManager userManager,
        [FromServices] ILibraryManager libraryManager)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(libraryManager);
        var byPath = request is not null && string.IsNullOrEmpty(request.ItemId) && !string.IsNullOrWhiteSpace(request.Path);
        var itemId = Guid.Empty;
        if (request is null || (!byPath && !Guid.TryParse(request.ItemId, out itemId)) || request.Path?.Length > 4096)
        {
            return BadRequest("A request body with an ItemId or a Path is required");
        }

        var (peer, refusal) = Sender(request.ServerId);
        if (peer is null)
        {
            return refusal!;
        }

        if (byPath)
        {
            var found = libraryManager.FindByPath(request.Path!, false);
            if (found is null)
            {
                return Ok(new LinkStreamCheckResponse { Allowed = false, Reason = "the file is not on this server" });
            }

            itemId = found.Id;
        }

        var verdict = HeldToScopedUser(policy.Check(peer, itemId, request.UserId, request.SourceUserId, request.Probe), userManager);
        if (!verdict.Allowed || verdict.Item is null)
        {
            return Ok(new LinkStreamCheckResponse { Allowed = false, Reason = verdict.Reason });
        }

        long size = 0;
        try
        {
            size = new FileInfo(verdict.Item.Path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The stream reads it again and answers for itself, so a size it cannot read now is left out.
        }

        return Ok(new LinkStreamCheckResponse { Allowed = true, ItemId = verdict.Item.Id.ToString("N", System.Globalization.CultureInfo.InvariantCulture), Size = size });
    }

    /// <summary>
    /// Receives playback of a link from the server that holds it, and shows it here as a session for the
    /// user it streams as.
    /// </summary>
    /// <param name="report">The report.</param>
    /// <param name="sessions">Applies the report.</param>
    /// <returns>No content, or why the report was refused.</returns>
    [HttpPost("Playback")]
    [RequireCompatibleProtocol]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Playback([FromBody] PeerPlaybackReport report, [FromServices] LinkPlaybackSessions sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (report is null || report.SessionKey?.Length > 256 || report.ClientName?.Length > 256)
        {
            return BadRequest("A playback report is required");
        }

        var (peer, refusal) = Sender(report.ServerId);
        if (peer is null)
        {
            return refusal!;
        }

        // A standard user's key only reports for that user.
        if (PeerCallerGuard.ScopedUser(HttpContext) is { } scoped
            && LinkPolicy.UserFor(peer, report.UserId, report.SourceUserId) != scoped)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "the source's key for this server belongs to another user");
        }

        var why = await sessions.ApplyAsync(peer, report, HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty).ConfigureAwait(false);
        if (why is not null)
        {
            _logger.LogDebug("Ignored a playback report from '{Server}': {Reason}", peer.DisplayName, why);
            return StatusCode(StatusCodes.Status403Forbidden, why);
        }

        return NoContent();
    }

    /// <summary>
    /// Passes a remote control command from the source's dashboard to a viewer of one of its files here.
    /// The source may only control a session that is playing one of its own links here.
    /// </summary>
    /// <param name="request">The command.</param>
    /// <param name="sessionManager">The sessions here.</param>
    /// <param name="libraryManager">The library.</param>
    /// <param name="links">This server's links.</param>
    /// <param name="grants">Which server each viewer streams from.</param>
    /// <returns>No content, or why the command was refused.</returns>
    [HttpPost("Control")]
    [RequireCompatibleProtocol]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Control(
        [FromBody] PeerControlRequest request,
        [FromServices] MediaBrowser.Controller.Session.ISessionManager sessionManager,
        [FromServices] ILibraryManager libraryManager,
        [FromServices] ILinkRegistry links,
        [FromServices] LinkGrants grants)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(grants);
        if (request is null || string.IsNullOrWhiteSpace(request.SessionKey) || request.Text?.Length > 1024 || request.Header?.Length > 256)
        {
            return BadRequest("A control request with a SessionKey is required");
        }

        var (peer, refusal) = Sender(request.ServerId);
        if (peer is null)
        {
            return refusal!;
        }

        var session = sessionManager.Sessions.FirstOrDefault(s => string.Equals(s.Id, request.SessionKey, StringComparison.Ordinal));
        var playing = session?.NowPlayingItem?.Id is { } nowId ? libraryManager.GetItemById(nowId)?.Path : null;
        // Only the server the viewer streams from, which after a fallback is not the link's own.
        if (session is null || playing is null || links.Find(playing) is not { } link
            || !string.Equals((grants.RouteFor(link.LinkId, session.UserId) ?? LinkFallback.PrimaryOf(link)).ServerKey, peer.Key, StringComparison.OrdinalIgnoreCase))
        {
            return NotFound("no session here is playing a link to that server");
        }

        var aborted = HttpContext.RequestAborted;
        if (!string.IsNullOrEmpty(request.Command))
        {
            if (!Enum.TryParse<MediaBrowser.Model.Session.PlaystateCommand>(request.Command, true, out var command))
            {
                return BadRequest("Unknown command");
            }

            await sessionManager.SendPlaystateCommand(null!, session.Id, new MediaBrowser.Model.Session.PlaystateRequest { Command = command, SeekPositionTicks = request.SeekPositionTicks }, aborted).ConfigureAwait(false);
        }
        else if (!string.IsNullOrEmpty(request.Text))
        {
            await sessionManager.SendMessageCommand(null!, session.Id, new MediaBrowser.Model.Session.MessageCommand { Header = string.IsNullOrWhiteSpace(request.Header) ? peer.DisplayName : $"{peer.DisplayName}: {request.Header}", Text = request.Text, TimeoutMs = request.TimeoutMs }, aborted).ConfigureAwait(false);
        }

        _logger.LogInformation("Passed a remote control command from '{Server}' to a viewer of {Path}", peer.DisplayName, playing);
        return NoContent();
    }

    // A standard user's key is held to that user: it streams as them, and its probe reads only what they can see.
    private LinkVerdict HeldToScopedUser(LinkVerdict verdict, IUserManager userManager)
    {
        if (!verdict.Allowed || PeerCallerGuard.ScopedUser(HttpContext) is not { } scoped)
        {
            return verdict;
        }

        if (verdict.UserId is { } user)
        {
            return user == scoped ? verdict : LinkVerdict.Refuse("the source's key for this server belongs to another user");
        }

        return userManager.GetUserById(scoped) is { } owner && verdict.Item?.IsVisible(owner) == true
            ? verdict
            : LinkVerdict.Refuse("the file is no longer on the source");
    }

    /// <summary>
    /// Answers for files the calling server holds links to: whether each is still here to stream, its
    /// size, and, when asked, its media info, chapters, and which trickplay images this server holds.
    /// The calling server checks its links against this on every content sync, and fills in a link's
    /// media info from it rather than probing the file across the network.
    /// </summary>
    /// <param name="request">The items.</param>
    /// <param name="libraryManager">The library.</param>
    /// <param name="userManager">The users here.</param>
    /// <param name="mediaSources">Reads an item's streams and attachments.</param>
    /// <param name="chapters">Reads an item's chapters.</param>
    /// <param name="trickplay">Reads an item's trickplay sets.</param>
    /// <returns>One answer per item.</returns>
    [HttpPost("Links/Describe")]
    [RequireCompatibleProtocol]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PeerLinkDescribeResponse>> DescribeLinks(
        [FromBody] PeerLinkDescribeRequest request,
        [FromServices] ILibraryManager libraryManager,
        [FromServices] IUserManager userManager,
        [FromServices] IMediaSourceManager mediaSources,
        [FromServices] MediaBrowser.Controller.Chapters.IChapterManager chapters,
        [FromServices] MediaBrowser.Controller.Trickplay.ITrickplayManager trickplay)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(mediaSources);
        ArgumentNullException.ThrowIfNull(chapters);
        ArgumentNullException.ThrowIfNull(trickplay);
        if (request?.ItemIds is null || request.ItemIds.Count > PeerLinkDescribeRequest.MaxItems)
        {
            return BadRequest($"A request body naming at most {PeerLinkDescribeRequest.MaxItems} items is required");
        }

        var (peer, refusal) = Sender(request.ServerId);
        if (peer is null)
        {
            return refusal!;
        }

        if (!peer.IsEnabled || !peer.AllowLinking)
        {
            return StatusCode(StatusCodes.Status403Forbidden, $"linking is not allowed for {peer.DisplayName} here");
        }

        // A standard user's key only learns about what that user can see.
        var scoped = PeerCallerGuard.ScopedUser(HttpContext) is { } scopedId ? userManager.GetUserById(scopedId) : null;
        var response = new PeerLinkDescribeResponse();
        foreach (var id in request.ItemIds)
        {
            var answer = new PeerLinkDescription { ItemId = id };
            response.Items.Add(answer);
            var item = Guid.TryParse(id, out var itemId) ? libraryManager.GetItemById(itemId) : null;
            if (item is null || item is Folder || string.IsNullOrEmpty(item.Path) || (PeerCallerGuard.ScopedUser(HttpContext) is not null && (scoped is null || !item.IsVisible(scoped))))
            {
                answer.Reason = "the file is no longer on the source";
                continue;
            }

            if (item.Path.EndsWith(LinkPaths.Extension, StringComparison.OrdinalIgnoreCase))
            {
                answer.Reason = "the item is a link on the source, not a file";
                continue;
            }

            try
            {
                var file = new FileInfo(item.Path);
                if (!file.Exists)
                {
                    answer.Reason = "the file is no longer on the source";
                    continue;
                }

                answer.Size = file.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                answer.Reason = "the source cannot read the file";
                continue;
            }

            answer.Exists = true;
            if (!request.Details)
            {
                continue;
            }

            answer.HasDetails = true;
            answer.RunTimeTicks = item.RunTimeTicks;
            answer.Container = item.Container;
            answer.TotalBitrate = item.TotalBitrate;
            answer.Streams = mediaSources.GetMediaStreams(item.Id).Where(stream => !stream.IsExternal).ToList();
            answer.Attachments = mediaSources.GetMediaAttachments(item.Id).ToList();
            answer.Chapters = chapters.GetChapters(item.Id)
                .Select(chapter => new PeerChapter
                {
                    StartPositionTicks = chapter.StartPositionTicks,
                    Name = chapter.Name,
                    HasImage = !string.IsNullOrEmpty(chapter.ImagePath) && System.IO.File.Exists(chapter.ImagePath)
                })
                .ToList();
            answer.Trickplay = (await trickplay.GetTrickplayResolutions(item.Id).ConfigureAwait(false))
                .Values
                .Select(info => new PeerTrickplay
                {
                    Width = info.Width,
                    Height = info.Height,
                    TileWidth = info.TileWidth,
                    TileHeight = info.TileHeight,
                    ThumbnailCount = info.ThumbnailCount,
                    Interval = info.Interval,
                    Bandwidth = info.Bandwidth
                })
                .ToList();
        }

        return Ok(response);
    }

    /// <summary>
    /// Sends one image this server made for a file the calling server links to: a chapter's image, or a
    /// trickplay tile. Jellyfin makes neither for a link, so the server holding the link takes them
    /// from here.
    /// </summary>
    /// <param name="itemId">This server's id of the item.</param>
    /// <param name="kind">Chapter or Trickplay.</param>
    /// <param name="index">The chapter's index, or the tile's.</param>
    /// <param name="serverId">The calling server's Jellyfin id.</param>
    /// <param name="width">For a trickplay tile, the width of the set it belongs to.</param>
    /// <param name="libraryManager">The library.</param>
    /// <param name="userManager">The users here.</param>
    /// <param name="chapters">Reads an item's chapters.</param>
    /// <param name="trickplay">Finds a trickplay tile.</param>
    /// <returns>The image.</returns>
    [HttpGet("Links/{itemId}/Images/{kind}/{index}")]
    [RequireCompatibleProtocol]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> LinkImage(
        [FromRoute] Guid itemId,
        [FromRoute] string kind,
        [FromRoute] int index,
        [FromQuery] string? serverId,
        [FromQuery] int width,
        [FromServices] ILibraryManager libraryManager,
        [FromServices] IUserManager userManager,
        [FromServices] MediaBrowser.Controller.Chapters.IChapterManager chapters,
        [FromServices] MediaBrowser.Controller.Trickplay.ITrickplayManager trickplay)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(chapters);
        ArgumentNullException.ThrowIfNull(trickplay);
        var (peer, refusal) = Sender(serverId);
        if (peer is null)
        {
            return refusal!;
        }

        if (!peer.IsEnabled || !peer.AllowLinking)
        {
            return StatusCode(StatusCodes.Status403Forbidden, $"linking is not allowed for {peer.DisplayName} here");
        }

        var item = libraryManager.GetItemById(itemId);
        if (item is null || index < 0
            || (PeerCallerGuard.ScopedUser(HttpContext) is { } scoped && (userManager.GetUserById(scoped) is not { } owner || !item.IsVisible(owner))))
        {
            return NotFound();
        }

        string? path = null;
        if (string.Equals(kind, "Chapter", StringComparison.OrdinalIgnoreCase))
        {
            path = chapters.GetChapters(item.Id).ElementAtOrDefault(index)?.ImagePath;
        }
        else if (string.Equals(kind, "Trickplay", StringComparison.OrdinalIgnoreCase))
        {
            path = await trickplay.GetTrickplayTilePathAsync(item, width, index, libraryManager.GetLibraryOptions(item).SaveTrickplayWithMedia).ConfigureAwait(false);
        }

        return !string.IsNullOrEmpty(path) && System.IO.File.Exists(path) ? PhysicalFile(path, "image/jpeg") : NotFound();
    }

    /// <summary>
    /// Lists this server's links, each with the path its full copy would have, so a peer reading this
    /// server's items matches a link by the file it stands in for rather than by its .strm.
    /// </summary>
    /// <param name="table">The content sync table.</param>
    /// <returns>The links.</returns>
    [HttpGet("Links")]
    [RequireCompatibleProtocol]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PeerLinksResponse> Links([FromServices] ContentSyncTableManager table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var links = table.GetLinks()
            .Select(row => LinkPaths.CopiedPathOf(row) is { } copied ? new PeerLink { LinkPath = row.LocalPath!, CopiedPath = copied } : null)
            .Where(link => link is not null)
            .Select(link => link!)
            .ToList();
        return Ok(new PeerLinksResponse { Links = links });
    }
}
