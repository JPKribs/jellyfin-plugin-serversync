using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Gives a link the media info its source already holds for the file: runtime, container, streams,
/// attachments, and chapters, saved the way Jellyfin's own probe saves them. The link then shows its
/// details without the file being probed across the network. It also takes the chapter images and
/// trickplay tiles the source made, since Jellyfin makes neither for a .strm.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkMediaInfo
{
    // The most one image from the source may weigh. A trickplay tile is a few hundred kilobytes.
    private const long MaxImageBytes = 32L * 1024 * 1024;

    // The most of each kind a source's answer is taken for. A real file is far inside these, so an
    // answer past them is cut off rather than followed.
    private const int MaxStreams = 200;
    private const int MaxChapters = 500;
    private const int MaxTrickplaySets = 8;
    private const int MaxTilesPerSet = 5000;
    private const int MaxTextLength = 400;

    private readonly IServiceProvider _services;
    private readonly ILibraryManager _libraryManager;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<LinkMediaInfo> _logger;
    private readonly Lazy<Stores?> _stores;

    public LinkMediaInfo(
        IServiceProvider services,
        ILibraryManager libraryManager,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        IServerApplicationHost applicationHost,
        ILogger<LinkMediaInfo> logger)
    {
        _services = services;
        _libraryManager = libraryManager;
        _configManager = configManager;
        _clientFactory = clientFactory;
        _applicationHost = applicationHost;
        _logger = logger;
        _stores = new Lazy<Stores?>(FindStores);
    }

    /// <summary>Whether a link's item has no media info yet.</summary>
    public static bool Lacks(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.RunTimeTicks is null or <= 0;
    }

    /// <summary>
    /// The streams to save for a link: the ones inside the source's file, between this server's own
    /// external audio and its other external files, numbered in that order, which is the order
    /// Jellyfin's probe saves them in.
    /// </summary>
    /// <param name="local">The streams the link's item has now.</param>
    /// <param name="shipped">The streams the source holds for the file.</param>
    /// <returns>The streams, renumbered.</returns>
    public static List<MediaStream> Merge(IReadOnlyList<MediaStream> local, IReadOnlyList<MediaStream> shipped)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(shipped);
        // A stream from the source names nothing on this server: it is a track inside the file, so any
        // path or address it carries is dropped rather than saved for Jellyfin to open later.
        var inside = shipped.Where(s => s is not null && !s.IsExternal).Take(MaxStreams).ToList();
        foreach (var stream in inside)
        {
            stream.Path = null;
            stream.DeliveryUrl = null;
            stream.IsExternalUrl = null;
            stream.Codec = Short(stream.Codec);
            stream.Language = Short(stream.Language);
            stream.Title = Short(stream.Title);
            stream.Comment = Short(stream.Comment);
        }

        var merged = local.Where(s => s.IsExternal && s.Type == MediaStreamType.Audio)
            .Concat(inside)
            .Concat(local.Where(s => s.IsExternal && s.Type != MediaStreamType.Audio))
            .ToList();
        for (var i = 0; i < merged.Count; i++)
        {
            merged[i].Index = i;
        }

        return merged;
    }

    private static string? Short(string? text)
        => text is { Length: > MaxTextLength } ? text[..MaxTextLength] : text;

    /// <summary>
    /// Asks a link's own server for the file's media info and saves it on the link's item. False when
    /// the server could not give it, so the caller can fall back to probing the file.
    /// </summary>
    /// <param name="item">The link's item here.</param>
    /// <param name="link">The link.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the item now has the source's media info.</returns>
    public async Task<bool> ShipAsync(BaseItem item, LinkEntry link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(link);
        if (_configManager.Configuration.FindServer(link.ServerKey) is not { IsEnabled: true, IsConfigured: true } server)
        {
            return false;
        }

        try
        {
            using var client = _clientFactory.Create(server);
            var answer = await client.DescribeLinksAsync(
                new PeerLinkDescribeRequest { ServerId = _applicationHost.SystemId, ItemIds = { link.LinkId }, Details = true },
                cancellationToken).ConfigureAwait(false);
            return answer?.Items.FirstOrDefault() is { Exists: true } description
                && await ApplyAsync(item, description, client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not take the media info for the link {Path} from '{Server}'", link.LinkPath, server.DisplayName);
            return false;
        }
    }

    /// <summary>
    /// Saves the source's media info for a file on the link's item, and takes the source's chapter
    /// images and trickplay tiles when that setting is on. False when the answer holds nothing to save.
    /// </summary>
    /// <param name="item">The link's item here.</param>
    /// <param name="description">The source's answer for the file.</param>
    /// <param name="client">The source, for its images, or null to take none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the media info was saved.</returns>
    public async Task<bool> ApplyAsync(BaseItem item, PeerLinkDescription description, SourceServerClient? client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(description);
        if (item is not Video video || !description.HasDetails || description.RunTimeTicks is not > 0 || description.Streams is not { Count: > 0 }
            || _stores.Value is not { } stores)
        {
            return false;
        }

        var streams = Merge(stores.MediaSources.GetMediaStreams(video.Id), description.Streams);
        var picture = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        video.RunTimeTicks = description.RunTimeTicks;
        video.Container = description.Container;
        video.TotalBitrate = description.TotalBitrate;
        video.Height = picture?.Height ?? 0;
        video.Width = picture?.Width ?? 0;
        video.DefaultVideoStreamIndex = picture?.Index;
        video.HasSubtitles = streams.Any(s => s.Type == MediaStreamType.Subtitle);
        stores.Streams.SaveMediaStreams(video.Id, streams, cancellationToken);
        stores.Attachments.SaveMediaAttachments(video.Id, description.Attachments ?? new List<MediaAttachment>(), cancellationToken);
        await video.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);

        var takeImages = client is not null && _configManager.Configuration.TakeLinkImagesFromSource;
        var chapters = new List<ChapterInfo>();
        for (var i = 0; i < Math.Min(description.Chapters?.Count ?? 0, MaxChapters); i++)
        {
            var from = description.Chapters![i];
            if (from.StartPositionTicks >= video.RunTimeTicks)
            {
                continue;
            }

            var chapter = new ChapterInfo { StartPositionTicks = from.StartPositionTicks, Name = Short(from.Name) };
            if (takeImages && from.HasImage)
            {
                var path = stores.Paths.GetChapterImagePath(video, from.StartPositionTicks);
                if (File.Exists(path) || await FetchImageAsync(client!, description.ItemId, "Chapter", i, 0, path, cancellationToken).ConfigureAwait(false))
                {
                    chapter.ImagePath = path;
                    chapter.ImageDateModified = File.GetLastWriteTimeUtc(path);
                }
            }

            chapters.Add(chapter);
        }

        stores.Chapters.SaveChapters(video, chapters);
        if (takeImages)
        {
            await TakeTrickplayAsync(stores.Trickplay, client!, video, description, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Took the media info for the link {Path} from its source", video.Path);
        return true;
    }

    // Jellyfin's own stores for media info. They are looked up when first needed, so a Jellyfin that
    // lacks one leaves links to the probe rather than stopping this plugin from loading.
    private Stores? FindStores()
    {
        if (_services.GetService<IMediaSourceManager>() is { } mediaSources
            && _services.GetService<IMediaStreamRepository>() is { } streams
            && _services.GetService<IMediaAttachmentRepository>() is { } attachments
            && _services.GetService<IChapterManager>() is { } chapters
            && _services.GetService<ITrickplayManager>() is { } trickplay
            && _services.GetService<IPathManager>() is { } paths)
        {
            return new Stores(mediaSources, streams, attachments, chapters, trickplay, paths);
        }

        _logger.LogWarning("This Jellyfin does not offer what is needed to save a link's media info from its source, so links are probed instead");
        return null;
    }

    private sealed record Stores(IMediaSourceManager MediaSources, IMediaStreamRepository Streams, IMediaAttachmentRepository Attachments, IChapterManager Chapters, ITrickplayManager Trickplay, IPathManager Paths);

    // A set is recorded only once every one of its tiles is here, so a player never asks for one that is missing.
    private async Task TakeTrickplayAsync(ITrickplayManager trickplay, SourceServerClient client, Video video, PeerLinkDescription description, CancellationToken cancellationToken)
    {
        if (description.Trickplay is not { Count: > 0 })
        {
            return;
        }

        var held = await trickplay.GetTrickplayResolutions(video.Id).ConfigureAwait(false);
        var withMedia = _libraryManager.GetLibraryOptions(video)?.SaveTrickplayWithMedia ?? false;
        foreach (var set in description.Trickplay.Take(MaxTrickplaySets))
        {
            // The source's numbers decide how many images are fetched, so numbers no real set has are
            // passed over rather than followed.
            if (set is null || set.Width is <= 0 or > 4096 || set.Height is <= 0 or > 4096
                || set.TileWidth is <= 0 or > 100 || set.TileHeight is <= 0 or > 100 || set.Interval <= 0
                || set.TileCount() is 0 or > MaxTilesPerSet || held?.ContainsKey(set.Width) == true)
            {
                continue;
            }

            var folder = trickplay.GetTrickplayDirectory(video, set.TileWidth, set.TileHeight, set.Width, withMedia);
            var complete = true;
            for (var tile = 0; tile < set.TileCount() && complete; tile++)
            {
                complete = await FetchImageAsync(client, description.ItemId, "Trickplay", tile, set.Width, Path.Combine(folder, tile.ToString(CultureInfo.InvariantCulture) + ".jpg"), cancellationToken).ConfigureAwait(false);
            }

            if (!complete)
            {
                _logger.LogDebug("Left the {Width} wide trickplay images for {Path}: the source did not send them all", set.Width, video.Path);
                continue;
            }

            await trickplay.SaveTrickplayInfo(new TrickplayInfo
            {
                ItemId = video.Id,
                Width = set.Width,
                Height = set.Height,
                TileWidth = set.TileWidth,
                TileHeight = set.TileHeight,
                ThumbnailCount = set.ThumbnailCount,
                Interval = set.Interval,
                Bandwidth = set.Bandwidth
            }).ConfigureAwait(false);
        }
    }

    // Writes one image from the source to a path, whole or not at all. False when the source has none.
    private async Task<bool> FetchImageAsync(SourceServerClient client, string sourceItemId, string kind, int index, int width, string path, CancellationToken cancellationToken)
    {
        var temp = path + ".serversync-tmp";
        try
        {
            using var response = await client.OpenLinkImageAsync(sourceItemId, kind, index, width, _applicationHost.SystemId, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxImageBytes)
            {
                return false;
            }

            if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
            }

            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await using (file.ConfigureAwait(false))
                {
                    await StallWatchdogStream.CopyAsync(new CappedReadStream(body, MaxImageBytes), file, StallWatchdogStream.DefaultIdleTimeout, cancellationToken).ConfigureAwait(false);
                    if (file.Length == 0 || file.Length > MaxImageBytes)
                    {
                        return false;
                    }
                }
            }

            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not take a {Kind} image from the source", kind);
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temporary file is replaced the next time the image is taken.
            }
        }
    }
}
