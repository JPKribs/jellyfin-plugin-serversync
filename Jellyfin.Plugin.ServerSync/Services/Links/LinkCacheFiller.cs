using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Decides what goes into the link cache and downloads it, one file at a time, in the background.
/// Two things put a link's file in the cache: someone plays the link, or it is an episode ahead of the
/// one a user is watching. Items picked to always keep are full copies in the library instead, see
/// <see cref="AlwaysKeep"/>. Each download reads the
/// file from the link's own server through the route links stream from, so that server's speed limits
/// apply, and this server's own download speed limit applies as well.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkCacheFiller : BackgroundService
{
    private readonly LinkCache _cache;
    private readonly ILinkRegistry _links;
    private readonly IServiceProvider _services;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LinkCacheFiller> _logger;
    private readonly Channel<Fill> _queue = Channel.CreateUnbounded<Fill>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> _waiting = new(StringComparer.OrdinalIgnoreCase);
    // Per link, the sessions playing it now. A file someone is watching is never dropped to make room.
    private readonly Dictionary<string, HashSet<string>> _playing = new(StringComparer.OrdinalIgnoreCase);

    public LinkCacheFiller(
        LinkCache cache,
        ILinkRegistry links,
        IServiceProvider services,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        IServerApplicationHost applicationHost,
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        ILogger<LinkCacheFiller> logger)
    {
        _cache = cache;
        _links = links;
        _services = services;
        _configManager = configManager;
        _clientFactory = clientFactory;
        _applicationHost = applicationHost;
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>Gets how many files wait to be cached.</summary>
    public int Waiting => _waiting.Count;

    /// <summary>
    /// The episodes that follow one in its show, in order, up to a count. Specials, which have no place
    /// in the order, are passed over.
    /// </summary>
    /// <param name="episodes">Every episode of the show.</param>
    /// <param name="current">The episode being watched.</param>
    /// <param name="count">How many to take.</param>
    /// <returns>The episodes ahead.</returns>
    public static IReadOnlyList<Episode> Ahead(IEnumerable<Episode> episodes, Episode current, int count)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(current);
        if (count <= 0 || current.ParentIndexNumber is not { } season || current.IndexNumber is not { } number)
        {
            return Array.Empty<Episode>();
        }

        return episodes
            .Where(e => e.ParentIndexNumber is > 0 && e.IndexNumber is not null && e.Id != current.Id)
            .Where(e => e.ParentIndexNumber > season || (e.ParentIndexNumber == season && e.IndexNumber > number))
            .OrderBy(e => e.ParentIndexNumber)
            .ThenBy(e => e.IndexNumber)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// Whether two files are versions of one film: in the same folder, each named after it, as in
    /// <c>Film (2020)/Film (2020) - 4K.mkv</c> and <c>Film (2020)/Film (2020) - 1080p.mkv</c>. A film is
    /// picked by its first version's file, and picking it keeps every version.
    /// </summary>
    /// <param name="pickedPath">The path of the item that was picked.</param>
    /// <param name="path">A link's source path.</param>
    /// <returns>True for two versions of one film.</returns>
    public static bool SameFilm(string? pickedPath, string? path)
    {
        if (string.IsNullOrEmpty(pickedPath) || string.IsNullOrEmpty(path) || !Path.HasExtension(pickedPath))
        {
            return false;
        }

        var picked = pickedPath.Replace('\\', '/');
        var other = path.Replace('\\', '/');
        var cut = picked.LastIndexOf('/');
        if (cut <= 0 || other.LastIndexOf('/') != cut || !other.StartsWith(picked[..(cut + 1)], StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var folder = picked[(picked.LastIndexOf('/', cut - 1) + 1)..cut];
        return folder.Length > 0
            && picked[(cut + 1)..].StartsWith(folder, StringComparison.OrdinalIgnoreCase)
            && other[(cut + 1)..].StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The library mapping a link row came through, or null when it is no longer set up.</summary>
    public static LibraryMapping? MappingFor(PluginConfiguration config, SyncItem row)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(row);
        return config.FindServer(row.ServerKey) is { IsEnabled: true } server
            ? server.GetEnabledLibraryMappings().FirstOrDefault(m => string.Equals(m.SourceLibraryId, row.SourceLibraryId, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Downloads a stopped server cut short are cleared. Whole cached files are kept.
        try
        {
            _cache.Tidy();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not tidy the link cache at startup");
        }

        _sessionManager.PlaybackStart += OnStart;
        _sessionManager.PlaybackStopped += OnStopped;
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnStart;
        _sessionManager.PlaybackStopped -= OnStopped;
        _queue.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Brings the cache in line with the settings: forgets files whose link is gone or became a copy,
    /// and applies retention and the size limit. Run by every content sync.
    /// </summary>
    public void Reconcile()
    {
        if (!_cache.Enabled)
        {
            return;
        }

        var config = _configManager.Configuration;
        // Read so that a table that cannot be read throws. Taken for no links, it would empty the cache.
        // A link on its way to a copy keeps its cached file until the copy is in place.
        var rows = Table().GetLinksStrict().Where(r => r.StorageMode.IsLink()).ToList();
        var byId = rows.ToDictionary(r => r.SourceItemId, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _cache.List())
        {
            // The link was removed, became a copy, or its library left the cache. A link the link check
            // found the source will not stream, such as with linking turned off there, is dropped too,
            // so a cached file does not outlive the source's say for longer than one content sync.
            if (!byId.TryGetValue(entry.LinkId, out var row) || MappingFor(config, row) is not { ExcludeFromLinkCache: false } || LinkVerifier.Flagged(row))
            {
                _cache.Remove(entry.LinkId);
            }
        }

        _cache.MakeRoom(0, Playing());
    }

    /// <summary>Queues a link's file to be cached. False when it is cached or waiting already.</summary>
    public bool Enqueue(string linkId, CacheReason reason, Guid? userId)
    {
        if (!_cache.Enabled || _cache.Holds(linkId) || !_waiting.TryAdd(linkId, 0))
        {
            return false;
        }

        if (_queue.Writer.TryWrite(new Fill(linkId, reason, userId)))
        {
            return true;
        }

        _waiting.TryRemove(linkId, out _);
        return false;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var fill in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await FillAsync(fill, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not cache the link {LinkId}", fill.LinkId);
                }
                finally
                {
                    _waiting.TryRemove(fill.LinkId, out _);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private ContentSyncTableManager Table() => _services.GetRequiredService<ContentSyncTableManager>();

    private HashSet<string> Playing()
    {
        lock (_playing)
        {
            return _playing.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    // The item a playback event is about. A film held in several versions reports the film, and names
    // the version being played as its media source.
    private BaseItem? Played(PlaybackProgressEventArgs e)
        => Guid.TryParse(e.MediaSourceId, out var version) && version != e.Item?.Id && _libraryManager.GetItemById(version) is { } item
            ? item
            : e.Item;

    private static string SessionOf(PlaybackProgressEventArgs e) => e.Session?.Id ?? e.DeviceId ?? e.PlaySessionId ?? string.Empty;

    private void OnStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            var played = Played(e);
            if (played?.Path is not { } path || !path.EndsWith(LinkPaths.Extension, StringComparison.OrdinalIgnoreCase) || _links.Find(path) is not { } link)
            {
                return;
            }

            // Noted whether or not the cache is on, so a viewer who started before it was switched on
            // is still known to be watching.
            lock (_playing)
            {
                if (!_playing.TryGetValue(link.LinkId, out var sessions))
                {
                    _playing[link.LinkId] = sessions = new HashSet<string>(StringComparer.Ordinal);
                }

                sessions.Add(SessionOf(e));
            }

            if (!_cache.Enabled)
            {
                return;
            }

            _cache.Played(link.LinkId);
            var user = e.Users?.FirstOrDefault();
            Enqueue(link.LinkId, CacheReason.Played, user?.Id);

            // The episodes after this one are cached ahead, so the next press of play is local.
            var ahead = _configManager.Configuration.LinkCachePreCacheEpisodes;
            if (ahead > 0 && played is Episode episode && episode.SeriesId != Guid.Empty)
            {
                var episodes = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    AncestorIds = new[] { episode.SeriesId },
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    Recursive = true,
                    IsVirtualItem = false
                }).OfType<Episode>();
                foreach (var next in Ahead(episodes, episode, ahead))
                {
                    if (next.Path is { } nextPath && _links.Find(nextPath) is { } nextLink)
                    {
                        Enqueue(nextLink.LinkId, CacheReason.Ahead, user?.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not queue a played link for the cache");
        }
    }

    private void OnStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            if (Played(e)?.Path is { } path && path.EndsWith(LinkPaths.Extension, StringComparison.OrdinalIgnoreCase) && _links.Find(path) is { } link)
            {
                _cache.Played(link.LinkId);
                lock (_playing)
                {
                    if (_playing.TryGetValue(link.LinkId, out var sessions) && sessions.Remove(SessionOf(e)) && sessions.Count == 0)
                    {
                        _playing.Remove(link.LinkId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not note that a link stopped playing");
        }
    }

    private async Task FillAsync(Fill fill, CancellationToken cancellationToken)
    {
        var config = _configManager.Configuration;
        if (!_cache.Enabled || _cache.Holds(fill.LinkId)
            || Table().GetBySourceItemId(fill.LinkId) is not { StorageMode: StorageMode.Link, Status: SyncStatus.Synced } row
            || MappingFor(config, row) is not { ExcludeFromLinkCache: false }
            || config.FindServer(row.ServerKey) is not { IsEnabled: true, IsConfigured: true } server)
        {
            return;
        }

        var name = Path.GetFileName(row.SourcePath);
        // The size is what room is made for and what the download is held to, so a file whose size
        // the source never gave is not cached.
        if (row.SourceSize <= 0)
        {
            _logger.LogInformation("Did not cache {File}: its size on the source is not known", name);
            return;
        }

        if (row.SourceSize > config.GetLinkCacheSizeBytes())
        {
            _logger.LogInformation("Did not cache {File} ({Size}): the link cache has no room for it", name, FormatUtilities.FormatBytes(row.SourceSize));
            return;
        }

        var extension = Path.GetExtension(row.SourcePath);
        var part = _cache.PartPathFor(fill.LinkId, extension);
        using var client = _clientFactory.Create(server);
        try
        {
            // Read through the route links stream from, marked as a cache download: the source applies
            // its speed limits, and does not count it as someone watching.
            using var response = await client.OpenPeerStreamAsync(
                fill.LinkId,
                _applicationHost.SystemId,
                new LinkGrant(fill.UserId, fill.UserId is null),
                LinkAccess.SourceUserFor(server, fill.UserId),
                null,
                false,
                true,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Did not cache {File}: '{Server}' answered {Status}", name, server.DisplayName, (int)response.StatusCode);
                return;
            }

            // Room is made only once the source has agreed to send the file, so a source that is down
            // or refuses never costs the cache what it already holds.
            if (!_cache.MakeRoom(row.SourceSize, Playing()))
            {
                _logger.LogInformation("Did not cache {File} ({Size}): the link cache has no room for it", name, FormatUtilities.FormatBytes(row.SourceSize));
                return;
            }

            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await using (file.ConfigureAwait(false))
                {
                    var speed = config.GetEffectiveDownloadSpeedBytes();
                    await StallWatchdogStream.CopyAsync(
                        new CappedReadStream(body, row.SourceSize),
                        file,
                        StallWatchdogStream.DefaultIdleTimeout,
                        (from, to, token) => StreamUtilities.CopyWithSpeedLimitAsync(from, to, speed, token),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            var expected = row.SourceSize;
            if (_cache.Complete(fill.LinkId, extension, expected, fill.Reason))
            {
                _logger.LogInformation("Cached {File} ({Size}) from '{Server}'", name, FormatUtilities.FormatBytes(expected), server.DisplayName);
            }
            else
            {
                _logger.LogWarning("Did not cache {File}: what '{Server}' sent was not the size of the file", name, server.DisplayName);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Did not cache {File}: {Reason}", name, ex.Message);
        }
        finally
        {
            try
            {
                File.Delete(part);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Cleared by the next tidy.
            }
        }
    }

    private sealed record Fill(string LinkId, CacheReason Reason, Guid? UserId);
}
