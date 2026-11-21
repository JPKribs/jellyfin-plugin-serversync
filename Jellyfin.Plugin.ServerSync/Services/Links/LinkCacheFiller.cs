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
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
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
/// Decides what goes into the link cache and downloads it in the background, one file at a time, when
/// someone plays a link or it is an episode ahead of the one a user is watching. Each download reads the
/// file through the route links stream from, so the source's speed limits apply as well as this server's.
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

    /// <summary>
    /// The sessions playing each link, noted even while the cache is off. A file someone is watching is
    /// never dropped to make room.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _playing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkCacheFiller"/> class.
    /// </summary>
    /// <param name="cache">The link cache to fill.</param>
    /// <param name="links">Tells which paths are this server's links.</param>
    /// <param name="services">Resolves the content sync table on demand.</param>
    /// <param name="configManager">Reads the plugin configuration.</param>
    /// <param name="clientFactory">Builds clients for other servers.</param>
    /// <param name="applicationHost">Names this server.</param>
    /// <param name="sessionManager">Raises the playback events that queue a file.</param>
    /// <param name="libraryManager">Finds the version played and the episodes ahead.</param>
    /// <param name="logger">Writes log entries.</param>
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
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(applicationHost);
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(logger);
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

    /// <summary>The library mapping a link row came through, or null when it is no longer set up.</summary>
    /// <param name="config">The plugin configuration that holds the mappings.</param>
    /// <param name="row">The link's row, which names its server and source library.</param>
    /// <returns>The mapping, or null.</returns>
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
        // A cache that cannot be tidied must not stop the server from starting.
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
    /// Brings the cache in line with the settings by forgetting files whose link is gone or became a copy
    /// and applying retention and the size limit. A table that cannot be read throws, since taking it for
    /// no links would empty the cache.
    /// </summary>
    public void Reconcile()
    {
        if (!_cache.Enabled)
        {
            return;
        }

        var config = _configManager.Configuration;
        var rows = Table().GetLinksStrict().Where(r => r.StorageMode.IsLink()).ToList();
        var byId = rows.ToDictionary(r => r.SourceItemId, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _cache.List())
        {
            // A link gone, excluded, flagged, or changed in size loses its cached file.
            if (!byId.TryGetValue(entry.LinkId, out var row) || MappingFor(config, row) is not { ExcludeFromLinkCache: false } || LinkVerifier.Flagged(row)
                || (row.SourceSize > 0 && entry.Size != row.SourceSize))
            {
                _cache.Remove(entry.LinkId);
            }
        }

        _cache.MakeRoom(0, Playing());
    }

    /// <summary>Queues a link's file to be cached, unless it is cached or waiting already.</summary>
    /// <param name="linkId">The link's id.</param>
    /// <param name="reason">Why the file is cached.</param>
    /// <param name="userId">The user whose play asked for it, or null.</param>
    /// <returns><c>true</c> when the file was queued.</returns>
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
                // One file that fails must not stop the others from caching.
                try
                {
                    await FillAsync(fill, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Could not cache the link {LinkId}", FormatUtilities.SanitizeForLog(fill.LinkId));
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

    /// <summary>Resolves the content sync table, which is not built when this service is.</summary>
    private ContentSyncTableManager Table() => _services.GetRequiredService<ContentSyncTableManager>();

    /// <summary>The links being played, which must not be dropped to make room.</summary>
    private HashSet<string> Playing()
    {
        lock (_playing)
        {
            return _playing.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The item a playback event is about. A film held in several versions reports the film, and names
    /// the version being played as its media source.
    /// </summary>
    private BaseItem? Played(PlaybackProgressEventArgs e)
        => Guid.TryParse(e.MediaSourceId, out var version) && version != e.Item?.Id && _libraryManager.GetItemById(version) is { } item
            ? item
            : e.Item;

    /// <summary>The key of the session a playback event came from.</summary>
    private static string SessionOf(PlaybackProgressEventArgs e) => e.Session?.Id ?? e.DeviceId ?? e.PlaySessionId ?? string.Empty;

    /// <summary>Notes who is playing a link, and queues it and the episodes ahead for the cache.</summary>
    private void OnStart(object? sender, PlaybackProgressEventArgs e)
    {
        // This runs in Jellyfin's playback event, which must never see a throw.
        try
        {
            var played = Played(e);
            if (played?.Path is not { } path || !path.EndsWith(LinkPaths.Extension, StringComparison.OrdinalIgnoreCase) || _links.Find(path) is not { } link)
            {
                return;
            }

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

    /// <summary>Notes that a session stopped playing a link.</summary>
    private void OnStopped(object? sender, PlaybackStopEventArgs e)
    {
        // This runs in Jellyfin's playback event, which must never see a throw.
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

    /// <summary>
    /// Downloads one link's file into the cache through the route links stream from, marked as a cache
    /// download so the source applies its speed limits and counts no viewer.
    /// </summary>
    private async Task FillAsync(Fill fill, CancellationToken cancellationToken)
    {
        var config = _configManager.Configuration;

        // Read before the row, so a change on the source while this downloads throws the download away.
        var version = _cache.VersionOf(fill.LinkId);
        if (!_cache.Enabled || _cache.Holds(fill.LinkId)
            || Table().GetBySourceItemId(fill.LinkId) is not { StorageMode: StorageMode.Link, Status: SyncStatus.Synced } row
            || MappingFor(config, row) is not { ExcludeFromLinkCache: false }
            || config.FindServer(row.ServerKey) is not { IsEnabled: true, IsConfigured: true } server)
        {
            return;
        }

        var name = FormatUtilities.SanitizeForLog(Path.GetFileName(row.SourcePath));

        // Room is made for the size, so a file of unknown size is skipped.
        if (row.SourceSize <= 0)
        {
            _logger.LogDebug("Did not cache {File}: its size on the source is not known", name);
            return;
        }

        if (row.SourceSize > _cache.LimitBytes())
        {
            _logger.LogDebug("Did not cache {File} ({Size}): the link cache has no room for it", name, FormatUtilities.FormatBytes(row.SourceSize));
            return;
        }

        var extension = Path.GetExtension(row.SourcePath);
        var part = _cache.PartPathFor(fill.LinkId, extension);
        using var client = _clientFactory.Create(server);
        try
        {
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
                _logger.LogDebug("Did not cache {File}: '{Server}' answered {Status}", name, server.DisplayName, (int)response.StatusCode);
                return;
            }

            // Room is made only once the source agrees, so a refusal costs nothing.
            if (!_cache.MakeRoom(row.SourceSize, Playing()))
            {
                _logger.LogDebug("Did not cache {File} ({Size}): the link cache has no room for it", name, FormatUtilities.FormatBytes(row.SourceSize));
                return;
            }

            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await using (file.ConfigureAwait(false))
                {
                    var speed = config.GetEffectiveDownloadSpeedBytes();
                    await StreamUtilities.CopyWithIdleTimeoutAsync(
                        new CappedReadStream(body, row.SourceSize),
                        file,
                        StreamUtilities.DefaultIdleTimeout,
                        (from, to, token) => StreamUtilities.CopyWithSpeedLimitAsync(from, to, speed, token),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            if (_cache.Complete(fill.LinkId, extension, row.SourceSize, fill.Reason, version))
            {
                _logger.LogDebug("Cached {File} ({Size}) from '{Server}'", name, FormatUtilities.FormatBytes(row.SourceSize), server.DisplayName);
            }
            else
            {
                _logger.LogWarning("Did not cache {File}: what '{Server}' sent was not the size of the file, or the file changed there meanwhile", name, server.DisplayName);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Did not cache {File}", name);
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

    /// <summary>One file waiting to be cached, why, and the user whose play asked for it.</summary>
    private sealed record Fill(string LinkId, CacheReason Reason, Guid? UserId);
}
