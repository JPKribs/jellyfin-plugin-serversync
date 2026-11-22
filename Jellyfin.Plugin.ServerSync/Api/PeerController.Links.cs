using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Api;

/// <summary>
/// The routes behind links, which stream this server's files to a linking server and list this server's own links.
/// </summary>
public partial class PeerController
{
    /// <summary>
    /// Streams one of this server's files to a linking server, with range support.
    /// The caller must be paired and allowed to link here, and the user it streams for must pass this server's checks and limits.
    /// </summary>
    /// <param name="itemId">The item's ID on this server.</param>
    /// <param name="serverId">The Jellyfin ID of the linking server.</param>
    /// <param name="userId">The user's ID on the linking server, or empty for its media probe.</param>
    /// <param name="sourceUserId">The user here the linking server maps them to, when it does.</param>
    /// <param name="probe">Whether this is the linking server's media probe.</param>
    /// <param name="fill">Whether the linking server is filling its link cache, which is held to the speed limits and not counted as a stream.</param>
    /// <param name="policy">Decides whether the server and user may stream the file.</param>
    /// <param name="sessions">Counts the open link streams against the limits.</param>
    /// <param name="userManager">Finds the user a standard caller is held to.</param>
    /// <param name="statistics">Counts a stream that starts, for the Statistics tab.</param>
    /// <param name="download">Whether the user is downloading the file, which also needs download permission here.</param>
    /// <returns>The file, or 403 or 429 with the reason.</returns>
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
        [FromQuery] bool fill,
        [FromServices] LinkPolicy policy,
        [FromServices] LinkSessions sessions,
        [FromServices] IUserManager userManager,
        [FromServices] Services.Database.SyncStatisticsStore? statistics = null,
        [FromQuery] bool download = false)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(sessions);
        var (peer, refusal) = Sender(serverId);
        if (peer is null)
        {
            return refusal!;
        }

        var verdict = HeldToScopedUser(policy.Check(peer, itemId, userId, sourceUserId, probe, watching: !fill, download: download), userManager);
        if (!verdict.Allowed || verdict.Item is null)
        {
            _logger.LogWarning("Refused a link stream to '{Server}': {Reason}", JPKribs.Jellyfin.Base.FormatUtilities.SanitizeForLog(peer.DisplayName), JPKribs.Jellyfin.Base.FormatUtilities.SanitizeForLog(verdict.Reason, 300));
            return StatusCode(StatusCodes.Status403Forbidden, verdict.Reason);
        }

        // A cache download is nobody watching, so it takes no stream from the limits.
        if (verdict.UserId is { } user && !fill)
        {
            // The limits are checked again under the lock that opens the stream.
            var handle = sessions.TryOpen(peer.Key, user, verdict.Item.Id, peer.LinkMaxStreams, peer.GetLinkMaxStreamsFor(user), policy.OwnPlayback(user, peer.LinkCountOwnSessions), out var started);
            if (handle is null)
            {
                return StatusCode(StatusCodes.Status403Forbidden, "the source's link stream limit is reached");
            }

            if (started)
            {
                CountLinkStream(statistics, peer.Key);
            }

            HttpContext.Response.RegisterForDispose(handle);
        }

        // Cache downloads are uncounted, so only a few run at once for each server.
        if (fill)
        {
            var filling = sessions.TryOpenFill(peer.Key);
            if (filling is null)
            {
                return StatusCode(StatusCodes.Status429TooManyRequests, "this server is already sending that server as many cache downloads as it may at once");
            }

            HttpContext.Response.RegisterForDispose(filling);
        }

        var mime = MediaBrowser.Model.Net.MimeTypes.GetMimeType(verdict.Item.Path);

        // A probe gets an allowance that reads media info but cannot copy the file.
        if (verdict.UserId is null && !fill)
        {
            var probed = verdict.Item.Id;
            if (!sessions.TakeProbe(peer.Key, probed, 0))
            {
                return StatusCode(StatusCodes.Status429TooManyRequests, "that server has read as much of this file as a probe may. A stream for a user is not limited this way");
            }

            var probeFile = new FileStream(verdict.Item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new FileStreamResult(new Utilities.MeteredReadStream(probeFile, bytes => sessions.TakeProbe(peer.Key, probed, bytes)), mime)
            {
                EnableRangeProcessing = true,
                LastModified = System.IO.File.GetLastWriteTimeUtc(verdict.Item.Path)
            };
        }

        var speed = peer.GetLinkStreamSpeedBytes(_configManager.Configuration.IsInBandwidthSchedule(), verdict.UserId);
        if (speed <= 0)
        {
            return PhysicalFile(verdict.Item.Path, mime, enableRangeProcessing: true);
        }

        var file = new FileStream(verdict.Item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new FileStreamResult(new JPKribs.Jellyfin.Base.ThrottledReadStream(file, sessions.SpeedFor(peer.Key, verdict.UserId, fill, speed)), mime)
        {
            EnableRangeProcessing = true,
            LastModified = System.IO.File.GetLastWriteTimeUtc(verdict.Item.Path)
        };
    }

    /// <summary>
    /// Counts a link stream that just started toward today, for the Statistics tab. A failure to count is logged and never fails the stream.
    /// </summary>
    private void CountLinkStream(Services.Database.SyncStatisticsStore? statistics, string serverKey)
    {
        try
        {
            statistics?.CountLinkStream(DateOnly.FromDateTime(DateTime.Now), serverKey);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Could not count a link stream for the Statistics tab");
        }
    }

    /// <summary>
    /// Answers whether a user on the linking server may stream one of this server's files, with the same checks as the stream.
    /// The file is named by its ID, or by its path when the caller looks for the same file to fall back to.
    /// </summary>
    /// <param name="request">The file, by ID or path, and the user to check for.</param>
    /// <param name="policy">Decides whether the server and user may stream the file.</param>
    /// <param name="userManager">Finds the user a standard caller is held to.</param>
    /// <param name="libraryManager">Finds a file by its path.</param>
    /// <param name="sessions">Tracks the link streams that are open, or null when none are kept.</param>
    /// <returns>Whether the stream is allowed, with the item's ID and size when it is.</returns>
    [HttpPost("Stream/Check")]
    [RequireCompatibleProtocol]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<LinkStreamCheckResponse> CheckStream(
        [FromBody] LinkStreamCheckRequest request,
        [FromServices] LinkPolicy policy,
        [FromServices] IUserManager userManager,
        [FromServices] ILibraryManager libraryManager,
        [FromServices] LinkSessions? sessions = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(libraryManager);
        var byPath = request is not null && string.IsNullOrEmpty(request.ItemId) && !string.IsNullOrWhiteSpace(request.Path);
        var itemId = Guid.Empty;
        if (request is null || (!byPath && !Guid.TryParse(request.ItemId, out itemId)) || request.Path?.Length > 4096)
        {
            return BadRequest("A request body with an ItemId or a Path is required.");
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

        var verdict = HeldToScopedUser(policy.Check(peer, itemId, request.UserId, request.SourceUserId, request.Probe, download: request.Download), userManager);
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
            // The stream reads the file itself, so an unreadable size is left out.
        }

        return Ok(new LinkStreamCheckResponse
        {
            Allowed = true,
            ItemId = verdict.Item.Id.ToString("N", System.Globalization.CultureInfo.InvariantCulture),
            Size = size,
            StreamsInUse = sessions?.CountForPeer(peer.Key) ?? 0,
            StreamLimit = Math.Max(0, peer.LinkMaxStreams),
            DownloadAllowed = request.Download
        });
    }

    /// <summary>
    /// Receives playback of a link from the linking server, and shows it here as a session for the user it streams as.
    /// </summary>
    /// <param name="report">What the linking server reports about the playback.</param>
    /// <param name="sessions">Applies the report.</param>
    /// <returns>204, or 403 with why the report was refused.</returns>
    [HttpPost("Playback")]
    [RequireCompatibleProtocol]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Playback([FromBody] PeerPlaybackReport report, [FromServices] LinkPlaybackSessions sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (report is null || report.SessionKey?.Length > 256 || report.ClientName?.Length > 256 || report.UserName?.Length > 256)
        {
            return BadRequest("A playback report is required.");
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

        var why = await sessions.ApplyAsync(
            peer,
            report,
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            Request.Headers[Services.Queue.HintProtocol.VersionHeader].ToString()).ConfigureAwait(false);
        if (why is not null)
        {
            _logger.LogDebug("Ignored a playback report from '{Server}': {Reason}", JPKribs.Jellyfin.Base.FormatUtilities.SanitizeForLog(peer.DisplayName), JPKribs.Jellyfin.Base.FormatUtilities.SanitizeForLog(why, 300));
            return StatusCode(StatusCodes.Status403Forbidden, why);
        }

        return NoContent();
    }

    /// <summary>
    /// Passes a remote control command from the source's dashboard to a session here that is playing one of its links.
    /// </summary>
    /// <param name="request">The session and the command or message to send it.</param>
    /// <param name="sessionManager">Finds the session and sends it the command.</param>
    /// <param name="libraryManager">Finds the path of what the session is playing.</param>
    /// <param name="links">Finds the link at that path.</param>
    /// <param name="grants">Knows which server each viewer streams from.</param>
    /// <returns>204, or why the command was refused.</returns>
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
            return BadRequest("A control request with a SessionKey is required.");
        }

        var (peer, refusal) = Sender(request.ServerId);
        if (peer is null)
        {
            return refusal!;
        }

        var session = sessionManager.Sessions.FirstOrDefault(s => string.Equals(s.Id, request.SessionKey, StringComparison.Ordinal));
        var playing = session?.NowPlayingItem?.Id is { } nowId ? libraryManager.GetItemById(nowId)?.Path : null;
        // Only the server the viewer streams from may control the session.
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
                return BadRequest("Unknown command.");
            }

            await sessionManager.SendPlaystateCommand(null!, session.Id, new MediaBrowser.Model.Session.PlaystateRequest { Command = command, SeekPositionTicks = request.SeekPositionTicks }, aborted).ConfigureAwait(false);
        }
        else if (!string.IsNullOrEmpty(request.Text))
        {
            await sessionManager.SendMessageCommand(null!, session.Id, new MediaBrowser.Model.Session.MessageCommand { Header = string.IsNullOrWhiteSpace(request.Header) ? peer.DisplayName : $"{peer.DisplayName}: {request.Header}", Text = request.Text, TimeoutMs = request.TimeoutMs }, aborted).ConfigureAwait(false);
        }

        _logger.LogDebug("Passed a remote control command from '{Server}' to a viewer of {Path}", JPKribs.Jellyfin.Base.FormatUtilities.SanitizeForLog(peer.DisplayName), JPKribs.Jellyfin.Base.FormatUtilities.SanitizeForLog(playing));
        return NoContent();
    }

    /// <summary>
    /// Holds a standard user's key to that user, so it streams as them and its probe reads only what they can see.
    /// </summary>
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
    /// Answers for files the linking server holds links to, with whether each is still here, its size, and its media info when asked.
    /// The linking server checks its links against this on every Content run.
    /// </summary>
    /// <param name="request">The item IDs to describe, and whether to include media info.</param>
    /// <param name="libraryManager">Finds each item.</param>
    /// <param name="userManager">Finds the user a standard caller is held to.</param>
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
            return BadRequest($"A request body naming at most {PeerLinkDescribeRequest.MaxItems} items is required.");
        }

        var (peer, refusal) = Sender(request.ServerId);
        if (peer is null)
        {
            return refusal!;
        }

        if (!peer.OffersLinks)
        {
            return StatusCode(StatusCodes.Status403Forbidden, $"linking is not allowed for {peer.DisplayName} here");
        }

        var scopedId = PeerCallerGuard.ScopedUser(HttpContext);
        var scoped = scopedId is { } id ? userManager.GetUserById(id) : null;
        var response = new PeerLinkDescribeResponse();
        foreach (var requestedId in request.ItemIds)
        {
            var answer = new PeerLinkDescription { ItemId = requestedId };
            response.Items.Add(answer);
            var item = Guid.TryParse(requestedId, out var itemId) ? libraryManager.GetItemById(itemId) : null;
            if (item is null || item is Folder || string.IsNullOrEmpty(item.Path) || (scopedId is not null && (scoped is null || !item.IsVisible(scoped))))
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
                answer.Reason = PeerLinkDescription.Unreadable;
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
    /// Sends a chapter image or trickplay tile this server made for a file the caller links to, since Jellyfin makes neither for a link.
    /// </summary>
    /// <param name="itemId">The item's ID on this server.</param>
    /// <param name="kind">The kind of image, Chapter or Trickplay.</param>
    /// <param name="index">The index of the chapter or the tile.</param>
    /// <param name="serverId">The Jellyfin ID of the linking server.</param>
    /// <param name="width">The width of the trickplay set the tile belongs to.</param>
    /// <param name="libraryManager">Finds the item and its library options.</param>
    /// <param name="userManager">Finds the user a standard caller is held to.</param>
    /// <param name="chapters">Reads an item's chapters.</param>
    /// <param name="trickplay">Finds a trickplay tile.</param>
    /// <returns>The image, or 404 when there is none.</returns>
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

        if (!peer.OffersLinks)
        {
            return StatusCode(StatusCodes.Status403Forbidden, $"linking is not allowed for {peer.DisplayName} here");
        }

        var item = libraryManager.GetItemById(itemId);
        if (item is null || index < 0
            || (PeerCallerGuard.ScopedUser(HttpContext) is { } scoped && (userManager.GetUserById(scoped) is not { } owner || !item.IsVisible(owner))))
        {
            return NotFound();
        }

        // A link's images came from another server, so they are not handed on.
        if (item.Path?.EndsWith(LinkPaths.Extension, StringComparison.OrdinalIgnoreCase) == true)
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
    /// Lists this server's links, each with the path its copy would have, so a peer matches a link by the file it stands in for.
    /// </summary>
    /// <param name="table">Reads the Content sync table.</param>
    /// <param name="userManager">Finds the user a standard caller is held to.</param>
    /// <param name="libraryManager">Finds the item behind each row, to check a standard user may see it.</param>
    /// <returns>The links, or 503 when the table cannot be read.</returns>
    [HttpGet("Links")]
    [RequireCompatibleProtocol]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PeerLinksResponse> Links([FromServices] ContentSyncTableManager table, [FromServices] IUserManager userManager, [FromServices] ILibraryManager libraryManager)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(libraryManager);

        var scoped = PeerCallerGuard.ScopedUser(HttpContext);
        var owner = scoped is { } id ? userManager.GetUserById(id) : null;
        if (owner is null && scoped is not null)
        {
            return Ok(new PeerLinksResponse());
        }

        // An empty list would tell the peer every .strm here is a file.
        if (table.TryGetLinks() is not { } rows)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "this server could not read its links");
        }

        var links = rows
            .Where(row => owner is null
                || (Guid.TryParse(row.LocalItemId, out var localId) && libraryManager.GetItemById(localId) is { } local && local.IsVisible(owner)))
            .Select(row => LinkPaths.CopiedPathOf(row) is { } copied ? new PeerLink { LinkPath = row.LocalPath!, CopiedPath = copied } : null)
            .OfType<PeerLink>()
            .ToList();
        return Ok(new PeerLinksResponse { Links = links });
    }

    /// <summary>
    /// Lists the IDs of media files that left this server's library since a time, since Jellyfin only answers what was added or changed.
    /// An ID names nothing to a caller that did not already hold the item.
    /// </summary>
    /// <param name="since">The time to answer from, in UTC.</param>
    /// <param name="removed">Holds the record of removed items.</param>
    /// <returns>The ids removed since, and how far back the record reaches.</returns>
    [HttpGet("Changes")]
    [RequireCompatibleProtocol]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<PeerChangesResponse> Changes([FromQuery] DateTime? since, [FromServices] RemovedItemsStore removed)
    {
        ArgumentNullException.ThrowIfNull(removed);
        if (since is not { } from)
        {
            return BadRequest("A since time is required.");
        }

        from = from.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(from, DateTimeKind.Utc) : from.ToUniversalTime();
        var (covered, ids) = removed.Since(from, DateTime.UtcNow);
        return Ok(new PeerChangesResponse { CoveredFrom = covered, TooOld = from < covered, RemovedItemIds = ids.ToList() });
    }

    /// <summary>
    /// Lists the copies this server downloaded from other servers, each with its origin, so a peer can tell them from this server's own files.
    /// </summary>
    /// <param name="table">Reads the Content sync table.</param>
    /// <param name="userManager">Finds the user a standard caller is held to.</param>
    /// <param name="libraryManager">Finds the item behind each row, to check a standard user may see it.</param>
    /// <returns>The copies, or 503 when the table cannot be read.</returns>
    [HttpGet("Copies")]
    [RequireCompatibleProtocol]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PeerCopiesResponse> Copies([FromServices] ContentSyncTableManager table, [FromServices] IUserManager userManager, [FromServices] ILibraryManager libraryManager)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(libraryManager);

        var scoped = PeerCallerGuard.ScopedUser(HttpContext);
        var owner = scoped is { } id ? userManager.GetUserById(id) : null;
        if (owner is null && scoped is not null)
        {
            return Ok(new PeerCopiesResponse());
        }

        // An empty list would tell the peer every file here is this server's own.
        if (table.TryGetCopiesWithOrigin() is not { } rows)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "this server could not read its copies");
        }

        var copies = rows
            .Where(row => owner is null || (libraryManager.FindByPath(row.LocalPath!, false) is { } local && local.IsVisible(owner)))
            .Select(row => new PeerCopy { Path = row.LocalPath!, OriginServerId = row.OriginServerId! })
            .ToList();

        // A removed copy has no item to check, so only full callers hear it.
        if (owner is null && HttpContext.RequestServices.GetService(typeof(RemovedItemsStore)) is RemovedItemsStore removedItems)
        {
            var listed = new System.Collections.Generic.HashSet<string>(copies.Select(copy => copy.Path), StringComparer.OrdinalIgnoreCase);
            copies.AddRange(removedItems.CopiesSince(DateTime.UtcNow.AddDays(-1))
                .Where(removed => listed.Add(removed.Key))
                .Select(removed => new PeerCopy { Path = removed.Key, OriginServerId = removed.Value }));
        }

        return Ok(new PeerCopiesResponse { Copies = copies });
    }
}
